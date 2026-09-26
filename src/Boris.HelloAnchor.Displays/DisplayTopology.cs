// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Displays;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Boris.HelloAnchor.Displays;

/// <summary>
/// Lists the monitors attached to the desktop (SPEC §7.4), joining the Connecting and Configuring Displays
/// (CCD) API, which knows each monitor's connector type and identity, with <c>EnumDisplayMonitors</c>, which
/// knows its work area.
/// </summary>
/// <remarks>
/// Coordinates are physical pixels only if the calling process is Per-Monitor V2 DPI aware; both the Agent
/// and the Settings app are.
/// </remarks>
public static class DisplayTopology
{
    /// <summary>How many times to retry <c>QueryDisplayConfig</c> if the topology changes mid-query.</summary>
    private const int QueryAttempts = 3;

    /// <summary>Registry key under which Windows keeps each monitor's device instance, including its EDID.</summary>
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>
    /// Returns the attached monitors in <c>QueryDisplayConfig</c> path order. Monitors the CCD query didn't
    /// describe (it failed, or a display was attached mid-query) are still listed, without an ID and not
    /// marked internal, so the Primary and DeviceName modes keep working.
    /// </summary>
    /// <param name="logger">Destination for Debug diagnostics; optional.</param>
    public static IReadOnlyList<DisplayMonitor> Enumerate(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var monitors = EnumerateMonitors();
        var targets = QueryTargets(logger);

        var result = new List<DisplayMonitor>(monitors.Count);
        foreach (var target in targets)
        {
            if (monitors.FirstOrDefault(m => string.Equals(m.DeviceName, target.SourceName, StringComparison.OrdinalIgnoreCase)) is not { } monitor)
            {
                continue;
            }

            var edid = ReadEdid(target.DevicePath, logger);
            var monitorId = edid?.MonitorId ?? MonitorIdentity.ModelFromDevicePath(target.DevicePath);
            var friendlyName = FirstNonEmpty(target.FriendlyName, edid?.Name)
                ?? (target.IsInternal ? "Built-in display" : "Unknown monitor");

            result.Add(new DisplayMonitor(monitor.DeviceName, friendlyName, monitorId, target.IsInternal, monitor.IsPrimary, monitor.Bounds, monitor.WorkArea));
        }

        // Any monitor CCD didn't describe.
        foreach (var monitor in monitors)
        {
            if (!result.Any(r => string.Equals(r.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new DisplayMonitor(monitor.DeviceName, monitor.DeviceName, null, false, monitor.IsPrimary, monitor.Bounds, monitor.WorkArea));
            }
        }

        return result;
    }

    /// <summary>Reads and parses a monitor's EDID from the registry; <see langword="null"/> if unavailable.</summary>
    private static EdidInfo? ReadEdid(string? devicePath, ILogger logger)
    {
        var instanceId = MonitorIdentity.InstanceIdFromDevicePath(devicePath);
        if (instanceId is null)
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{instanceId}\Device Parameters");
            return key?.GetValue("EDID") is byte[] edid ? MonitorIdentity.ParseEdid(edid) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug("Could not read the EDID for {InstanceId}: {Message}", instanceId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Queries the active display paths and describes each one's target (monitor) and source (GDI name).
    /// </summary>
    private static unsafe List<TargetEntry> QueryTargets(ILogger logger)
    {
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        var entries = new List<TargetEntry>();

        for (var attempt = 1; attempt <= QueryAttempts; attempt++)
        {
            var error = PInvoke.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != WIN32_ERROR.NO_ERROR)
            {
                logger.LogDebug("GetDisplayConfigBufferSizes failed: {Error}.", error);
                return entries;
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
                return entries;
            }

            for (var i = 0; i < pathCount; i++)
            {
                ref readonly var path = ref paths[i];

                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;

                var result = PInvoke.DisplayConfigGetDeviceInfo(&source.header);
                if (result != 0)
                {
                    logger.LogDebug("DisplayConfigGetDeviceInfo(GET_SOURCE_NAME) failed: {Error}.", result);
                    continue;
                }

                // The target name gives the monitor's identity. Without it the path is still usable by GDI name.
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;

                string? friendlyName = null;
                string? devicePath = null;
                result = PInvoke.DisplayConfigGetDeviceInfo(&target.header);
                if (result == 0)
                {
                    friendlyName = target.monitorFriendlyDeviceName.ToString();
                    devicePath = target.monitorDevicePath.ToString();
                }
                else
                {
                    logger.LogDebug("DisplayConfigGetDeviceInfo(GET_TARGET_NAME) failed: {Error}.", result);
                }

                entries.Add(new TargetEntry(
                    source.viewGdiDeviceName.ToString(),
                    devicePath,
                    friendlyName,
                    DisplayOutputTechnology.IsInternal((uint)path.targetInfo.outputTechnology)));
            }

            return entries;
        }

        logger.LogDebug("Display topology kept changing; gave up after {Attempts} attempts.", QueryAttempts);
        return entries;
    }

    /// <summary>Lists every monitor attached to the desktop with its device name, bounds and work area.</summary>
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
                    ToPixelRect(info.monitorInfo.rcMonitor),
                    ToPixelRect(info.monitorInfo.rcWork),
                    (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
            }
        }

        return monitors;
    }

    /// <summary>Converts a Win32 <see cref="RECT"/> to a <see cref="PixelRect"/>.</summary>
    private static PixelRect ToPixelRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    /// <summary>Returns the first value that isn't null or blank.</summary>
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>One monitor from <c>EnumDisplayMonitors</c>.</summary>
    private sealed record MonitorEntry(string DeviceName, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

    /// <summary>One active CCD path: its source's GDI name and its target monitor.</summary>
    private sealed record TargetEntry(string SourceName, string? DevicePath, string? FriendlyName, bool IsInternal);
}
