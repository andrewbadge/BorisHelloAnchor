// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Boris.HelloAnchor.Agent;

/// <summary>A resolved target monitor.</summary>
/// <param name="DeviceName">GDI device name, e.g. <c>\\.\DISPLAY1</c>.</param>
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
    /// <summary>How many times to retry <c>QueryDisplayConfig</c> if the topology changes mid-query.</summary>
    private const int QueryAttempts = 3;

    /// <summary>
    /// Returns the target display for the current configuration, or <see langword="null"/> if it isn't
    /// present (lid closed, internal panel disabled, named device unplugged).
    /// </summary>
    /// <param name="options">Current configuration.</param>
    public TargetDisplay? Resolve(HelloAnchorOptions options)
    {
        var monitors = EnumerateMonitors();

        switch (options.TargetDisplay)
        {
            case TargetDisplayMode.Primary:
                var primary = monitors.FirstOrDefault(m => m.IsPrimary);
                return primary is null ? null : new TargetDisplay(primary.DeviceName, primary.WorkArea);

            case TargetDisplayMode.DeviceName:
                return FindMonitor(monitors, options.TargetDeviceName);

            case TargetDisplayMode.Internal:
            default:
                var internalName = FindInternalDeviceName();
                if (internalName is null)
                {
                    logger.LogDebug("No active internal display found.");
                    return null;
                }

                return FindMonitor(monitors, internalName);
        }
    }

    /// <summary>Looks a monitor up by GDI device name.</summary>
    private TargetDisplay? FindMonitor(List<MonitorEntry> monitors, string? deviceName)
    {
        var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            logger.LogDebug("Display '{DeviceName}' is not currently attached to the desktop.", deviceName);
            return null;
        }

        return new TargetDisplay(match.DeviceName, match.WorkArea);
    }

    /// <summary>
    /// Uses the Connecting and Configuring Displays (CCD) API to find the first active path whose target
    /// is an internal panel, and returns the GDI name of its source (SPEC §7.4 "Internal").
    /// </summary>
    private unsafe string? FindInternalDeviceName()
    {
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;

        for (var attempt = 1; attempt <= QueryAttempts; attempt++)
        {
            var error = PInvoke.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != WIN32_ERROR.NO_ERROR)
            {
                logger.LogDebug("GetDisplayConfigBufferSizes failed: {Error}.", error);
                return null;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            error = PInvoke.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes);
            if (error == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                // A display was attached between the two calls; size the buffers again.
                continue;
            }

            if (error != WIN32_ERROR.NO_ERROR)
            {
                logger.LogDebug("QueryDisplayConfig failed: {Error}.", error);
                return null;
            }

            // The first matching path wins (SPEC §7.4); dual-panel laptops can pick another via DeviceName.
            for (var i = 0; i < pathCount; i++)
            {
                ref readonly var path = ref paths[i];
                if (!DisplayOutputTechnology.IsInternal((uint)path.targetInfo.outputTechnology))
                {
                    continue;
                }

                var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                request.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                request.header.adapterId = path.sourceInfo.adapterId;
                request.header.id = path.sourceInfo.id;

                var result = PInvoke.DisplayConfigGetDeviceInfo(&request.header);
                if (result != 0)
                {
                    logger.LogDebug("DisplayConfigGetDeviceInfo(GET_SOURCE_NAME) failed: {Error}.", result);
                    continue;
                }

                return request.viewGdiDeviceName.ToString();
            }

            return null;
        }

        logger.LogDebug("Display topology kept changing; gave up after {Attempts} attempts.", QueryAttempts);
        return null;
    }

    /// <summary>Lists every monitor attached to the desktop with its device name and work area.</summary>
    private static unsafe List<MonitorEntry> EnumerateMonitors()
    {
        var handles = new List<HMONITOR>();

        // EnumDisplayMonitors is synchronous, so a capturing lambda is safe; KeepAlive guards the delegate
        // until the native call has returned.
        MONITORENUMPROC callback = (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return true;
        };
        PInvoke.EnumDisplayMonitors(default, (RECT?)null, callback, default);
        GC.KeepAlive(callback);

        var monitors = new List<MonitorEntry>(handles.Count);
        foreach (var handle in handles)
        {
            // MONITORINFOEXW begins with MONITORINFO; cbSize tells Windows to fill the extended form.
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info))
            {
                monitors.Add(new MonitorEntry(
                    info.szDevice.ToString(),
                    info.monitorInfo.rcWork.ToPixelRect(),
                    (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
            }
        }

        return monitors;
    }

    /// <summary>One attached monitor.</summary>
    private sealed record MonitorEntry(string DeviceName, PixelRect WorkArea, bool IsPrimary);
}
