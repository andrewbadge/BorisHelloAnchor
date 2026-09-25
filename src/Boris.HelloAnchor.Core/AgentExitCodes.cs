// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Core;

/// <summary>
/// Process exit codes the agent reports to the service (SPEC §6.3).
/// </summary>
public static class AgentExitCodes
{
    /// <summary>The agent exited because shutdown was requested (event signalled or <c>WM_QUIT</c>).</summary>
    public const int ShutdownRequested = 0;

    /// <summary>An unhandled error terminated the agent.</summary>
    public const int Fatal = 1;

    /// <summary>Another agent already holds the per-session mutex.</summary>
    public const int AlreadyRunning = 2;
}

/// <summary>What the service should do after an agent process exits.</summary>
public enum AgentExitAction
{
    /// <summary>The exit was intentional; stop supervising the session until a new logon/connect event.</summary>
    Park,

    /// <summary>The exit was unexpected; relaunch after the configured backoff.</summary>
    Restart,
}

/// <summary>
/// Maps an agent exit code to the service's response (SPEC §6.3, exit-code table).
/// </summary>
public static class AgentRestartPolicy
{
    /// <summary>Decides whether an agent that exited with <paramref name="exitCode"/> should be restarted.</summary>
    /// <param name="exitCode">The raw process exit code from <c>GetExitCodeProcess</c>.</param>
    /// <returns><see cref="AgentExitAction.Park"/> for a requested shutdown or duplicate instance, otherwise <see cref="AgentExitAction.Restart"/>.</returns>
    public static AgentExitAction Decide(uint exitCode) => exitCode switch
    {
        AgentExitCodes.ShutdownRequested => AgentExitAction.Park,
        AgentExitCodes.AlreadyRunning => AgentExitAction.Park,
        // Everything else (1, crashes, 0xC0000005, being killed from Task Manager, torn down at logoff...)
        // is treated as unexpected. The service re-validates the session before actually relaunching.
        _ => AgentExitAction.Restart,
    };
}
