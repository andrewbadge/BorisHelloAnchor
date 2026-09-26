// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.


using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Core.Displays;
using Boris.HelloAnchor.Displays;
using Microsoft.Extensions.Logging;

namespace Boris.HelloAnchor.Agent;

/// <summary>A resolved target monitor.</summary>
/// <param name="DeviceName">GDI device name, e.g. <c>\.\DISPLAY1</c>.</param>
/// <param name="WorkArea">The monitor's work area (<c>rcWork</c>, excludes the taskbar), in physical pixels.</param>
internal sealed record TargetDisplay(string DeviceName, PixelRect WorkArea);

/// <summary>
/// Finds the monitor the prompt should be moved to (SPEC §7.4).
/// </summary>
/// <remarks>
/// Resolution runs on every matching prompt rather than being cached. It is cheap, and it means docking,
/// undocking, lid changes and resolution changes are handled without listening for display events.
/// </remarks>
internal sealed class DisplayResolver(ILogger logger)
{
    /// <summary>
    /// Returns the target display for the current configuration, or <see langword="null"/> if it isn't
    /// present. A configured display that isn't attached falls back to the built-in panel; <see langword="null"/>
    /// means neither is available (e.g. lid closed and the chosen monitor unplugged).
    /// </summary>
    /// <param name="options">Current configuration.</param>
    public TargetDisplay? Resolve(HelloAnchorOptions options)
    {
        var selection = TargetDisplaySelector.Select(DisplayTopology.Enumerate(logger), options);
        if (selection is null)
        {
            logger.LogDebug("No active display matches {Mode} and there is no active internal display.", options.TargetDisplay);
            return null;
        }

        if (selection.IsFallback)
        {
            logger.LogDebug(
                "Configured display ({Mode} {Target}) is not attached; using the internal display {Device}.",
                options.TargetDisplay,
                options.TargetDisplay == TargetDisplayMode.Monitor ? options.TargetMonitorId : options.TargetDeviceName,
                selection.Monitor.DeviceName);
        }

        return new TargetDisplay(selection.Monitor.DeviceName, selection.Monitor.WorkArea);
    }
}
