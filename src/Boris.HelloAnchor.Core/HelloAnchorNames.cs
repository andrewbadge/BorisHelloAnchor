// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Core;

/// <summary>
/// Names of kernel objects, the service and the event-log source, shared by every component (SPEC §5).
/// </summary>
public static class HelloAnchorNames
{
    /// <summary>Windows service name registered by the installer.</summary>
    public const string ServiceName = "Boris.HelloAnchor";

    /// <summary>Event Log source registered by the installer in the Application log.</summary>
    public const string EventLogSource = "Boris.HelloAnchor";

    /// <summary>
    /// Manual-reset event the service sets to ask every agent to exit. Created by the service with an ACL
    /// that lets authenticated users wait on it (SYNCHRONIZE) but not signal it (SPEC §6.5).
    /// </summary>
    public const string ShutdownEvent = @"Global\Boris.HelloAnchor.Shutdown";

    /// <summary>
    /// Per-session mutex that guarantees a single agent per session. The <c>Local\</c> namespace is
    /// scoped to the session, so each session has its own instance (SPEC §7.1).
    /// </summary>
    public const string AgentMutex = @"Local\Boris.HelloAnchor.Agent";

    /// <summary>File name of the agent executable, which the service resolves next to itself (SPEC §6.3).</summary>
    public const string AgentExecutable = "Boris.HelloAnchor.Agent.exe";

    /// <summary>
    /// Logger category used for service start/stop messages. The service routes this category to the
    /// Windows Event Log at Information level; every other category only reaches it at Warning or above.
    /// </summary>
    public const string LifecycleLogCategory = "Boris.HelloAnchor.Service.Lifecycle";
}
