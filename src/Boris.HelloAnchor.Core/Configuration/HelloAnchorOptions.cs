// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Microsoft.Extensions.Logging;

namespace Boris.HelloAnchor.Core.Configuration;

/// <summary>Which display the credential prompt should be moved to (SPEC §4).</summary>
public enum TargetDisplayMode
{
    /// <summary>The built-in laptop panel, detected via <c>QueryDisplayConfig</c>.</summary>
    Internal,

    /// <summary>Whichever monitor Windows currently treats as primary.</summary>
    Primary,

    /// <summary>The GDI device named by <see cref="HelloAnchorOptions.TargetDeviceName"/>.</summary>
    DeviceName,
}

/// <summary>
/// Immutable configuration shared by the service and the agent. Mirrors the <c>HelloAnchor</c> section
/// of <c>config.json</c> (SPEC §4). Reloads produce a new instance rather than mutating this one, so a
/// reader on another thread always sees a consistent snapshot.
/// </summary>
public sealed record HelloAnchorOptions
{
    /// <summary>The built-in defaults, used for any missing or invalid field.</summary>
    public static HelloAnchorOptions Default { get; } = new();

    /// <summary>Which display to move the prompt to.</summary>
    public TargetDisplayMode TargetDisplay { get; init; } = TargetDisplayMode.Internal;

    /// <summary>GDI device name (e.g. <c>\\.\DISPLAY1</c>); only used when <see cref="TargetDisplay"/> is <see cref="TargetDisplayMode.DeviceName"/>.</summary>
    public string? TargetDeviceName { get; init; }

    /// <summary>Process names (without <c>.exe</c>) whose windows are candidates.</summary>
    public IReadOnlyList<string> TargetProcessNames { get; init; } = ["CredentialUIBroker"];

    /// <summary>Window class names that must also match.</summary>
    public IReadOnlyList<string> TargetWindowClasses { get; init; } = ["Credential Dialog Xaml Host"];

    /// <summary>Delays, in milliseconds after handling a prompt, at which its position is re-checked. Sorted ascending.</summary>
    public IReadOnlyList<int> VerifyDelaysMs { get; init; } = [150, 300, 600];

    /// <summary>Whether to skip RDP sessions when launching agents.</summary>
    public bool SkipRemoteSessions { get; init; } = true;

    /// <summary>Whether standard users may get an agent running as SYSTEM (see SPEC §6.4).</summary>
    public bool AllowSystemTokenFallback { get; init; }

    /// <summary>Successive restart delays, in seconds, for a crashed agent; the last value repeats.</summary>
    public IReadOnlyList<int> AgentRestartBackoffSeconds { get; init; } = [2, 5, 15, 60];

    /// <summary>Minimum log level written to the log files.</summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
}
