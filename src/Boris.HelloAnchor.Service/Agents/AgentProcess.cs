// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Boris.HelloAnchor.Service.Agents;

/// <summary>How an agent was launched.</summary>
internal enum AgentTokenKind
{
    /// <summary>The user's elevated linked token (the normal case for admins with UAC).</summary>
    LinkedElevated,

    /// <summary>The user's own token, already high integrity (UAC off, or built-in Administrator).</summary>
    UserFull,

    /// <summary>The service's SYSTEM token moved into the user's session (SPEC §6.4 fallback).</summary>
    SystemFallback,
}

/// <summary>
/// A running agent: its process handle, identity and exit notification (SPEC §6.3 "AgentHandle").
/// </summary>
internal sealed class AgentProcess : IDisposable
{
    private readonly WaitHandle _waitHandle;
    private RegisteredWaitHandle? _registration;

    /// <summary>Wraps an already-running process.</summary>
    /// <param name="process">Process handle (owned by this object).</param>
    /// <param name="processId">Process ID.</param>
    /// <param name="sessionId">Session the agent runs in.</param>
    /// <param name="generation">Unique launch number, used to discard stale exit notifications.</param>
    /// <param name="tokenKind">How the agent was launched.</param>
    public AgentProcess(SafeProcessHandle process, uint processId, uint sessionId, long generation, AgentTokenKind tokenKind)
    {
        Handle = process;
        ProcessId = processId;
        SessionId = sessionId;
        Generation = generation;
        TokenKind = tokenKind;
        StartTimestamp = Stopwatch.GetTimestamp();

        // A WaitHandle view of the process handle, for ThreadPool.RegisterWaitForSingleObject. It does not
        // own the handle; Handle does.
        _waitHandle = new ProcessWaitHandle(process);
    }

    /// <summary>The process handle.</summary>
    public SafeProcessHandle Handle { get; }

    /// <summary>The process ID.</summary>
    public uint ProcessId { get; }

    /// <summary>The session the agent runs in.</summary>
    public uint SessionId { get; }

    /// <summary>Unique launch number.</summary>
    public long Generation { get; }

    /// <summary>Which token the agent was launched with.</summary>
    public AgentTokenKind TokenKind { get; }

    /// <summary><see cref="Stopwatch"/> timestamp at launch.</summary>
    public long StartTimestamp { get; }

    /// <summary>How long the agent has been running.</summary>
    public TimeSpan Uptime => Stopwatch.GetElapsedTime(StartTimestamp);

    /// <summary>
    /// Calls <paramref name="onExit"/> with the exit code once the process ends. Runs on a thread-pool thread.
    /// </summary>
    /// <param name="onExit">Callback receiving this object and the process exit code.</param>
    public void OnExit(Action<AgentProcess, uint> onExit)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _waitHandle,
            (_, _) => onExit(this, GetExitCode()),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the process to exit.</summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <returns><see langword="true"/> if the process has exited.</returns>
    public bool WaitForExit(TimeSpan timeout) => _waitHandle.WaitOne(timeout);

    /// <summary>Forcefully terminates the agent.</summary>
    /// <param name="exitCode">Exit code to report.</param>
    public void Terminate(uint exitCode) => PInvoke.TerminateProcess(Handle, exitCode);

    /// <summary>Stops the exit wait and closes the process handle. Does not terminate the process.</summary>
    public void Dispose()
    {
        _registration?.Unregister(null);
        _waitHandle.Dispose();
        Handle.Dispose();
    }

    /// <summary>Reads the process exit code (<c>STILL_ACTIVE</c>, 259, if it hasn't exited).</summary>
    private uint GetExitCode() => PInvoke.GetExitCodeProcess(Handle, out var code) ? code : uint.MaxValue;

    /// <summary>A non-owning <see cref="WaitHandle"/> over a process handle.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
