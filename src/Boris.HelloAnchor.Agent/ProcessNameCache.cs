// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Runtime.InteropServices.ComTypes;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Boris.HelloAnchor.Agent;

/// <summary>
/// Resolves a process ID to its image name (without extension), caching the answer (SPEC §7.3).
/// </summary>
/// <remarks>
/// PIDs are reused by Windows, so the cache key is PID <em>plus</em> process creation time: a recycled PID
/// has a different creation time and misses the cache. The lookup only happens after a window has already
/// matched a target class name, so it is rare and the cache stays tiny.
/// </remarks>
internal sealed class ProcessNameCache
{
    /// <summary>Upper bound on entries; the cache is simply cleared when exceeded.</summary>
    private const int MaxEntries = 64;

    private readonly Dictionary<uint, (long CreationTime, string Name)> _entries = [];

    /// <summary>
    /// Returns the image name of <paramref name="processId"/> without <c>.exe</c>, or <see langword="null"/>
    /// if the process has exited or cannot be queried.
    /// </summary>
    /// <param name="processId">Process to look up.</param>
    public string? GetName(uint processId)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION is granted for almost every process, including higher-integrity ones.
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        if (!PInvoke.GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            return null;
        }

        var creationTime = ToInt64(creation);
        if (_entries.TryGetValue(processId, out var cached) && cached.CreationTime == creationTime)
        {
            return cached.Name;
        }

        Span<char> buffer = stackalloc char[1024];
        var length = (uint)buffer.Length;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(buffer[..(int)length].ToString());
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[processId] = (creationTime, name);
        return name;
    }

    /// <summary>Combines the two halves of a <see cref="FILETIME"/>.</summary>
    private static long ToInt64(FILETIME time) => ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
}
