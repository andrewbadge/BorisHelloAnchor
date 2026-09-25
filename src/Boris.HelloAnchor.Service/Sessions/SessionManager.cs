// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Service.Agents;
using Windows.Win32.System.RemoteDesktop;

namespace Boris.HelloAnchor.Service.Sessions;

/// <summary>
/// Keeps exactly one agent running in every eligible interactive session (SPEC §6.2–§6.5).
/// </summary>
/// <remarks>
/// All bookkeeping happens on the single loop in <see cref="ExecuteAsync"/>. SCM callbacks, the
/// reconciliation timer, restart timers and process-exit waits only ever <em>post</em> events to the
/// <see cref="SessionEventQueue"/>, so the session dictionary needs no locks.
/// </remarks>
internal sealed class SessionManager(
    SessionEventQueue queue,
    AgentLauncher launcher,
    ShutdownSignal shutdown,
    ConfigWatcher config,
    ILogger<SessionManager> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    /// <summary>How often the session list is reconciled against reality (SPEC §6.2).</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long agents get to exit after the shutdown event is set (SPEC §6.5).</summary>
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Exit code used when an agent has to be terminated.</summary>
    private const uint TerminatedExitCode = 0xDEAD;

    /// <summary>Start/stop messages go to this category, which the Event Log receives at Information.</summary>
    private readonly ILogger _lifecycle = loggerFactory.CreateLogger(HelloAnchorNames.LifecycleLogCategory);

    /// <summary>Per-session state. Only touched on the loop thread (and in <see cref="StopAgents"/> after the loop ends).</summary>
    private readonly Dictionary<uint, SessionState> _sessions = [];

    private long _restartGeneration;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _lifecycle.LogInformation(
            "Boris HelloAnchor service {Version} starting. Agent: {AgentPath}.",
            typeof(SessionManager).Assembly.GetName().Version,
            AgentLauncher.AgentPath);
        logger.LogInformation("Configuration: {Options}", config.Current.Describe());
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the loop first so nothing launches new agents while we shut the existing ones down.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        StopAgents();
        _lifecycle.LogInformation("Boris HelloAnchor service stopped.");
    }

    /// <summary>The session loop.</summary>
    /// <param name="stoppingToken">Signalled when the service is stopping.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconcile immediately (covers sessions already logged on at start-up), then every 30 s.
        queue.Post(new SessionEvent(SessionEventKind.Reconcile));
        _ = PostReconcileTicksAsync(stoppingToken);

        try
        {
            await foreach (var sessionEvent in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Handle(sessionEvent);
                }
                catch (Exception ex)
                {
                    // One bad event must never stop supervision of every other session.
                    logger.LogError(ex, "Error handling {Event}.", sessionEvent);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Posts a <see cref="SessionEventKind.Reconcile"/> event every <see cref="ReconcileInterval"/>.</summary>
    private async Task PostReconcileTicksAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                queue.Post(new SessionEvent(SessionEventKind.Reconcile));
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping.
        }
    }

    /// <summary>Dispatches one event. Runs only on the loop thread.</summary>
    private void Handle(SessionEvent e)
    {
        switch (e.Kind)
        {
            case SessionEventKind.Logon:
            case SessionEventKind.ConsoleConnect:
            case SessionEventKind.RemoteConnect:
                logger.LogDebug("Session {Session}: {Kind}.", e.SessionId, e.Kind);
                EnsureAgent(e.SessionId, fromSessionEvent: true);
                break;

            case SessionEventKind.Logoff:
                logger.LogInformation("Session {Session}: logoff.", e.SessionId);
                ForgetSession(e.SessionId);
                break;

            case SessionEventKind.Reconcile:
                Reconcile();
                break;

            case SessionEventKind.AgentExited:
                OnAgentExited(e.SessionId, e.Generation, e.ExitCode);
                break;

            case SessionEventKind.RestartDue:
                OnRestartDue(e.SessionId, e.Generation);
                break;
        }
    }

    /// <summary>
    /// Makes tracked state match <c>WTSEnumerateSessions</c>: launch into active sessions without an agent,
    /// keep agents in disconnected sessions, drop sessions that no longer exist (SPEC §6.2).
    /// </summary>
    private void Reconcile()
    {
        List<WtsSessionInfo> sessions;
        try
        {
            sessions = WtsSessions.Enumerate();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "WTSEnumerateSessions failed; skipping reconciliation.");
            return;
        }

        var present = sessions.Select(s => s.SessionId).ToHashSet();
        foreach (var gone in _sessions.Keys.Where(id => !present.Contains(id)).ToList())
        {
            logger.LogInformation("Session {Session} no longer exists; forgetting it.", gone);
            ForgetSession(gone);
        }

        foreach (var session in sessions)
        {
            if (session.State == WTS_CONNECTSTATE_CLASS.WTSActive)
            {
                EnsureAgent(session.SessionId, fromSessionEvent: false);
            }
        }
    }

    /// <summary>
    /// Launches an agent into <paramref name="sessionId"/> unless one is running, a restart is pending, the
    /// session is parked, or it must be skipped (session 0, RDP).
    /// </summary>
    /// <param name="sessionId">Target session.</param>
    /// <param name="fromSessionEvent">True for logon/connect events, which clear a parked state.</param>
    private void EnsureAgent(uint sessionId, bool fromSessionEvent)
    {
        // Session 0 is where services live; nobody sees windows there.
        if (sessionId == 0)
        {
            return;
        }

        var state = GetOrAddState(sessionId);
        if (fromSessionEvent)
        {
            state.Parked = false;
        }

        if (state.Agent is not null || state.PendingRestartGeneration != 0 || state.Parked)
        {
            return;
        }

        var options = config.Current;
        if (options.SkipRemoteSessions && WtsSessions.IsRemote(sessionId))
        {
            state.WarnOnce(logger, "remote", LogLevel.Information, "Session {Session} is a remote session; not launching an agent (SkipRemoteSessions).", sessionId);
            return;
        }

        var result = launcher.TryLaunch(sessionId, options);
        if (result.Agent is { } agent)
        {
            state.Agent = agent;
            state.ClearWarnings();
            agent.OnExit((a, code) => queue.Post(new SessionEvent(SessionEventKind.AgentExited, a.SessionId, a.Generation, code)));
            logger.LogInformation(
                "Session {Session}: agent started (pid {Pid}, token {TokenKind}, restart #{RestartCount}).",
                sessionId,
                agent.ProcessId,
                agent.TokenKind,
                state.RestartCount);
            return;
        }

        switch (result.Failure)
        {
            case LaunchFailure.NoUserToken:
                // Normal for a session at the logon screen; Debug only.
                state.WarnOnce(logger, "no-token", LogLevel.Debug, "Session {Session}: no user token yet. {Detail}", sessionId, result.Detail);
                break;
            case LaunchFailure.NotElevatable:
                state.WarnOnce(logger, "not-elevatable", LogLevel.Warning, "Session {Session}: agent cannot be elevated for this user. {Detail}", sessionId, result.Detail);
                break;
            default:
                // Real errors are logged every time, but they only recur every reconciliation (30 s).
                logger.LogError("Session {Session}: failed to launch agent. {Detail}", sessionId, result.Detail);
                break;
        }
    }

    /// <summary>
    /// Applies the exit-code policy (SPEC §6.3): park on requested shutdown/duplicate, otherwise schedule a
    /// restart after backoff.
    /// </summary>
    private void OnAgentExited(uint sessionId, long generation, uint exitCode)
    {
        if (!_sessions.TryGetValue(sessionId, out var state) || state.Agent is not { } agent || agent.Generation != generation)
        {
            // An exit for an agent we have already forgotten (e.g. after logoff).
            return;
        }

        var uptime = agent.Uptime;
        state.Agent = null;
        agent.Dispose();

        if (AgentRestartPolicy.Decide(exitCode) == AgentExitAction.Park)
        {
            state.Parked = true;
            var level = exitCode == AgentExitCodes.AlreadyRunning ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "Session {Session}: agent exited with code {ExitCode}; not restarting until the next logon/connect.", sessionId, exitCode);
            return;
        }

        var count = RestartBackoff.EffectiveCount(state.RestartCount, uptime);
        var delay = RestartBackoff.GetDelay(count, config.Current.AgentRestartBackoffSeconds);
        state.RestartCount = count + 1;
        state.PendingRestartGeneration = Interlocked.Increment(ref _restartGeneration);

        logger.LogWarning(
            "Session {Session}: agent exited unexpectedly with code 0x{ExitCode:X} after {Uptime:g}; restarting in {Delay:g}.",
            sessionId,
            exitCode,
            uptime,
            delay);

        var restartGeneration = state.PendingRestartGeneration;
        _ = Task.Delay(delay).ContinueWith(
            _ => queue.Post(new SessionEvent(SessionEventKind.RestartDue, sessionId, restartGeneration)),
            TaskScheduler.Default);
    }

    /// <summary>
    /// A restart backoff elapsed. The session is re-checked <em>now</em>, not when the agent exited: at logoff
    /// the agent is often killed before the logoff notification arrives (SPEC §6.3).
    /// </summary>
    private void OnRestartDue(uint sessionId, long restartGeneration)
    {
        if (!_sessions.TryGetValue(sessionId, out var state) || state.PendingRestartGeneration != restartGeneration)
        {
            return;
        }

        state.PendingRestartGeneration = 0;
        var wtsState = WtsSessions.GetState(sessionId);
        if (wtsState is not (WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSDisconnected))
        {
            logger.LogInformation("Session {Session} is {State}; not restarting its agent.", sessionId, wtsState?.ToString() ?? "gone");
            ForgetSession(sessionId);
            return;
        }

        EnsureAgent(sessionId, fromSessionEvent: false);
    }

    /// <summary>Stops tracking a session. The agent is not killed: it ends with the session.</summary>
    private void ForgetSession(uint sessionId)
    {
        if (_sessions.Remove(sessionId, out var state))
        {
            state.Agent?.Dispose();
        }
    }

    /// <summary>Returns the state for a session, creating it on first use.</summary>
    private SessionState GetOrAddState(uint sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            state = new SessionState();
            _sessions[sessionId] = state;
        }

        return state;
    }

    /// <summary>
    /// Shutdown sequence (SPEC §6.5): set the event, give agents 3 s, then terminate the rest. Runs after the
    /// loop has ended, so it has the dictionary to itself.
    /// </summary>
    private void StopAgents()
    {
        var agents = _sessions.Values.Select(s => s.Agent).OfType<AgentProcess>().ToList();
        if (agents.Count > 0)
        {
            shutdown.Signal();
            var deadline = DateTime.UtcNow + GracefulExitTimeout;
            foreach (var agent in agents)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (!agent.WaitForExit(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                {
                    logger.LogWarning("Agent pid {Pid} in session {Session} did not exit in time; terminating.", agent.ProcessId, agent.SessionId);
                    agent.Terminate(TerminatedExitCode);
                }
            }
        }

        foreach (var state in _sessions.Values)
        {
            state.Agent?.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>Everything the service tracks about one session.</summary>
    private sealed class SessionState
    {
        private readonly HashSet<string> _warned = [];

        /// <summary>The running agent, if any.</summary>
        public AgentProcess? Agent { get; set; }

        /// <summary>Restarts performed in the current crash sequence (see <see cref="RestartBackoff"/>).</summary>
        public int RestartCount { get; set; }

        /// <summary>Non-zero while a restart is scheduled; identifies which restart request is current.</summary>
        public long PendingRestartGeneration { get; set; }

        /// <summary>
        /// Set when the agent exited deliberately (shutdown requested, duplicate instance). Reconciliation
        /// won't relaunch; the next logon/connect event clears it.
        /// </summary>
        public bool Parked { get; set; }

        /// <summary>Logs a message only the first time <paramref name="key"/> is seen for this session (SPEC §6.2).</summary>
        public void WarnOnce(ILogger logger, string key, LogLevel level, string message, params object?[] args)
        {
            if (_warned.Add(key))
            {
#pragma warning disable CA2254 // Template is a constant at every call site.
                logger.Log(level, message, args);
#pragma warning restore CA2254
            }
        }

        /// <summary>Forgets once-only warnings after a successful launch, so a later regression is reported again.</summary>
        public void ClearWarnings() => _warned.Clear();
    }
}
