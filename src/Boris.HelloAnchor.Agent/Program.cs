// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Boris.HelloAnchor.Agent;
using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Core.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using Windows.Win32;

// Entry point for Boris.HelloAnchor.Agent (SPEC §7).
//
// Normally launched by the service, elevated, into a user session with:  Boris.HelloAnchor.Agent.exe --session N
// For development it can also be started by hand from an elevated terminal; without the service it simply
// runs until closed (there is no shutdown event to wait on).

// The session is taken from the process itself, not trusted from the command line (SPEC §7.1).
var sessionId = PInvoke.ProcessIdToSessionId(PInvoke.GetCurrentProcessId(), out var sid) ? sid : 0u;
var claimedSession = ParseSessionArgument(args);

// Logging first, so everything after this point can be recorded. The level is refined once config loads.
var levelSwitch = HelloAnchorLogging.CreateLevelSwitch(LogLevel.Information);
using var serilog = HelloAnchorLogging.CreateFileLogger(HelloAnchorLogging.AgentLogFileName(sessionId), levelSwitch);
using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Trace) // Serilog's level switch does the real filtering.
    .AddProvider(new SerilogLoggerProvider(serilog, dispose: false)));
var logger = loggerFactory.CreateLogger("Boris.HelloAnchor.Agent");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; agent terminating.");

try
{
    logger.LogInformation(
        "Agent {Version} starting in session {Session} (pid {Pid}, elevated: {Elevated}).",
        typeof(AgentRuntime).Assembly.GetName().Version,
        sessionId,
        Environment.ProcessId,
        IsElevated());

    if (claimedSession is { } claimed && claimed != sessionId)
    {
        logger.LogWarning("--session {Claimed} does not match the actual session {Actual}; using {Actual}.", claimed, sessionId, sessionId);
    }

    // One agent per session (SPEC §7.1). Local\ is session-scoped.
    using var mutex = new Mutex(initiallyOwned: true, HelloAnchorNames.AgentMutex, out var createdNew);
    if (!createdNew)
    {
        logger.LogWarning("Another agent is already running in session {Session}; exiting.", sessionId);
        return AgentExitCodes.AlreadyRunning;
    }

    using var config = new ConfigWatcher(HelloAnchorPaths.ConfigFile, loggerFactory.CreateLogger<ConfigWatcher>());
    ApplyLogLevel(config.Current);
    config.Changed += (_, options) => ApplyLogLevel(options);
    logger.LogInformation("Configuration: {Options}", config.Current.Describe());

    using var shutdownEvent = OpenShutdownEvent(logger);
    var exitCode = new AgentRuntime(config, loggerFactory).Run(shutdownEvent);

    logger.LogInformation("Agent exiting with code {ExitCode}.", exitCode);
    return exitCode;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Agent failed.");
    return AgentExitCodes.Fatal;
}

// Pushes the configured log level into Serilog's switch (called on start and on every reload).
void ApplyLogLevel(HelloAnchorOptions options) =>
    levelSwitch.MinimumLevel = HelloAnchorLogging.ToSerilogLevel(options.LogLevel);

// Opens the service's shutdown event with SYNCHRONIZE only (SPEC §7.2 step 1). Returns null if the service
// isn't running, which is normal during development.
static EventWaitHandle? OpenShutdownEvent(Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        return EventWaitHandleAcl.OpenExisting(HelloAnchorNames.ShutdownEvent, EventWaitHandleRights.Synchronize);
    }
    catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
    {
        logger.LogInformation("Shutdown event '{Name}' not available ({Reason}); running standalone.", HelloAnchorNames.ShutdownEvent, ex.Message);
        return null;
    }
}

// Reads the optional "--session N" argument (diagnostics only).
static uint? ParseSessionArgument(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--session", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
    }

    return null;
}

// True if the process token is a member of the Administrators group with the group enabled (i.e. elevated),
// or is SYSTEM. Logged at start-up because a non-elevated agent cannot move the prompt.
static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
