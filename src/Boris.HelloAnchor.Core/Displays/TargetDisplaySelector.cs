// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core.Configuration;

namespace Boris.HelloAnchor.Core.Displays;

/// <summary>One monitor attached to the desktop.</summary>
/// <param name="DeviceName">GDI device name of its source, e.g. <c>\\.\DISPLAY1</c>. Not stable across docking.</param>
/// <param name="FriendlyName">Name to show people, e.g. <c>DELL U2723QE</c>.</param>
/// <param name="MonitorId">Stable ID (see <see cref="MonitorIdentity"/>), or <see langword="null"/> if it couldn't be worked out.</param>
/// <param name="IsInternal">Whether it is a built-in laptop panel.</param>
/// <param name="IsPrimary">Whether Windows treats it as the main display.</param>
/// <param name="Bounds">Full monitor rectangle, in physical pixels.</param>
/// <param name="WorkArea">Work area (<c>rcWork</c>, excludes the taskbar), in physical pixels.</param>
public sealed record DisplayMonitor(
    string DeviceName,
    string FriendlyName,
    string? MonitorId,
    bool IsInternal,
    bool IsPrimary,
    PixelRect Bounds,
    PixelRect WorkArea);

/// <summary>The monitor chosen for the prompt.</summary>
/// <param name="Monitor">The chosen monitor.</param>
/// <param name="IsFallback">
/// <see langword="true"/> if the configured display isn't attached and the built-in panel was used instead.
/// </param>
/// <param name="Profile">The profile whose choice was used, or <see langword="null"/> for the top-level setting.</param>
public sealed record DisplaySelection(DisplayMonitor Monitor, bool IsFallback, DisplayProfile? Profile = null);

/// <summary>
/// Picks the monitor to move the prompt to (SPEC §7.4). Pure logic over an already-enumerated list, so it can
/// be unit-tested.
/// </summary>
public static class TargetDisplaySelector
{
    /// <summary>
    /// Returns the target for <paramref name="options"/>, falling back to the built-in panel when the configured
    /// display isn't attached, or <see langword="null"/> if neither is available (e.g. lid closed and the chosen
    /// monitor unplugged).
    /// </summary>
    /// <param name="monitors">Attached monitors, in <c>QueryDisplayConfig</c> path order.</param>
    /// <param name="options">Current configuration.</param>
    public static DisplaySelection? Select(IReadOnlyList<DisplayMonitor> monitors, HelloAnchorOptions options)
    {
        var profile = FindProfile(monitors, options);
        if (profile is not null)
        {
            options = options with { TargetDisplay = profile.TargetDisplay, TargetMonitorId = profile.TargetMonitorId };
        }

        return SelectFor(monitors, options) is { } selection ? selection with { Profile = profile } : null;
    }

    /// <summary>
    /// Identifies a monitor setup: the IDs of the attached monitors, sorted. Monitors without an ID are left out.
    /// Closing the lid removes the built-in panel, so lid-open and lid-closed are different setups.
    /// </summary>
    /// <param name="monitors">Attached monitors.</param>
    public static IReadOnlyList<string> SetupKey(IReadOnlyList<DisplayMonitor> monitors) =>
        [.. monitors.Select(m => m.MonitorId).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Returns the profile saved for exactly this set of monitors, or <see langword="null"/>.</summary>
    /// <param name="monitors">Attached monitors.</param>
    /// <param name="options">Current configuration.</param>
    public static DisplayProfile? FindProfile(IReadOnlyList<DisplayMonitor> monitors, HelloAnchorOptions options)
    {
        var key = SetupKey(monitors);
        return key.Count == 0 ? null : options.Profiles.FirstOrDefault(p => SameSetup(p.Monitors, key));
    }

    /// <summary>True if two setup keys list the same monitors (order and case ignored).</summary>
    /// <param name="a">One setup key.</param>
    /// <param name="b">The other.</param>
    public static bool SameSetup(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the (already profile-resolved) choice in <paramref name="options"/>.</summary>
    private static DisplaySelection? SelectFor(IReadOnlyList<DisplayMonitor> monitors, HelloAnchorOptions options)
    {
        var chosen = options.TargetDisplay switch
        {
            TargetDisplayMode.Primary => monitors.FirstOrDefault(m => m.IsPrimary),
            TargetDisplayMode.DeviceName => monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, options.TargetDeviceName, StringComparison.OrdinalIgnoreCase)),
            TargetDisplayMode.Monitor => FindMonitor(monitors, options.TargetMonitorId),
            _ => null,
        };

        if (chosen is not null)
        {
            return new DisplaySelection(chosen, IsFallback: false);
        }

        // Internal is both a mode and the fallback for every other mode. With several built-in panels
        // (dual-screen laptops) the first in path order wins.
        var builtIn = monitors.FirstOrDefault(m => m.IsInternal);
        return builtIn is null ? null : new DisplaySelection(builtIn, IsFallback: options.TargetDisplay != TargetDisplayMode.Internal);
    }

    /// <summary>
    /// Finds the attached monitor for a configured ID: an exact match first, then a same-model match (see
    /// <see cref="MonitorIdentity.Match"/>).
    /// </summary>
    /// <param name="monitors">Attached monitors.</param>
    /// <param name="monitorId">Canonical configured ID.</param>
    public static DisplayMonitor? FindMonitor(IReadOnlyList<DisplayMonitor> monitors, string? monitorId)
    {
        if (monitorId is null)
        {
            return null;
        }

        return monitors.FirstOrDefault(m => MonitorIdentity.Match(m.MonitorId, monitorId) == MonitorMatch.Exact)
            ?? monitors.FirstOrDefault(m => MonitorIdentity.Match(m.MonitorId, monitorId) == MonitorMatch.Model);
    }
}
