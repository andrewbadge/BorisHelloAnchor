// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core.Configuration;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Boris.HelloAnchor.Agent;

/// <summary>
/// Decides whether a window-show event refers to the credential prompt (SPEC §7.3).
/// </summary>
/// <remarks>
/// <c>EVENT_OBJECT_SHOW</c> fires for every window, menu and tooltip on the desktop, so the checks run
/// cheapest-first: object IDs, then top-level, then class name, and only then the process lookup.
/// </remarks>
internal sealed class WindowFilter
{
    private readonly ProcessNameCache _processNames = new();

    /// <summary>
    /// Returns <see langword="true"/> if the event describes a top-level window whose class and owning
    /// process both match the configured targets.
    /// </summary>
    /// <param name="hwnd">Window from the event.</param>
    /// <param name="idObject">Object ID from the event; must be <c>OBJID_WINDOW</c>.</param>
    /// <param name="idChild">Child ID from the event; must be <c>CHILDID_SELF</c>.</param>
    /// <param name="options">Current configuration.</param>
    public bool IsTarget(HWND hwnd, int idObject, int idChild, HelloAnchorOptions options)
    {
        // 1. The event must be about the window itself, not a child accessibility object.
        if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != (int)PInvoke.CHILDID_SELF || hwnd.IsNull)
        {
            return false;
        }

        // 2. Only top-level windows.
        if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != hwnd)
        {
            return false;
        }

        // 3. Window class. Class names are case-insensitive in Win32.
        Span<char> buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(hwnd, buffer);
        if (length <= 0 || !MatchesAny(buffer[..length], options.TargetWindowClasses))
        {
            return false;
        }

        // 4. Owning process.
        _ = PInvoke.GetWindowThreadProcessId(hwnd, out var processId);
        var processName = processId == 0 ? null : _processNames.GetName(processId);
        return processName is not null && MatchesAny(processName, options.TargetProcessNames);
    }

    /// <summary>Case-insensitive membership test.</summary>
    private static bool MatchesAny(ReadOnlySpan<char> value, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
