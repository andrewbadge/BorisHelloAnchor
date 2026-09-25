// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;

namespace Boris.HelloAnchor.Service.Sessions;

/// <summary>A snapshot of one Terminal Services session.</summary>
/// <param name="SessionId">Session ID.</param>
/// <param name="State">Connection state (active, disconnected, ...).</param>
internal readonly record struct WtsSessionInfo(uint SessionId, WTS_CONNECTSTATE_CLASS State);

/// <summary>
/// Thin wrappers over the WTS session APIs (SPEC §6.2). Every buffer returned by WTS is freed here.
/// </summary>
internal static class WtsSessions
{
    /// <summary>Lists every session on this machine.</summary>
    /// <exception cref="Win32Exception"><c>WTSEnumerateSessions</c> failed.</exception>
    public static unsafe List<WtsSessionInfo> Enumerate()
    {
        if (!PInvoke.WTSEnumerateSessions(HANDLE.WTS_CURRENT_SERVER_HANDLE, 0, 1, out var buffer, out var count))
        {
            throw new Win32Exception();
        }

        try
        {
            var sessions = new List<WtsSessionInfo>((int)count);
            for (var i = 0; i < count; i++)
            {
                sessions.Add(new WtsSessionInfo(buffer[i].SessionId, buffer[i].State));
            }

            return sessions;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// Returns the session's connection state, or <see langword="null"/> if the session no longer exists.
    /// </summary>
    /// <param name="sessionId">Session to query.</param>
    public static unsafe WTS_CONNECTSTATE_CLASS? GetState(uint sessionId)
    {
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.WTS_CURRENT_SERVER_HANDLE, sessionId, WTS_INFO_CLASS.WTSConnectState, out var buffer, out var bytes))
        {
            return null;
        }

        try
        {
            return bytes >= sizeof(int) ? (WTS_CONNECTSTATE_CLASS)(*(int*)buffer.Value) : null;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the session is connected over a remote protocol
    /// (<c>WTSClientProtocolType</c> ≠ 0). Unknown is treated as "not remote".
    /// </summary>
    /// <param name="sessionId">Session to query.</param>
    public static unsafe bool IsRemote(uint sessionId)
    {
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.WTS_CURRENT_SERVER_HANDLE, sessionId, WTS_INFO_CLASS.WTSClientProtocolType, out var buffer, out var bytes))
        {
            return false;
        }

        try
        {
            // 0 = console, 1 = legacy ICA, 2 = RDP.
            return bytes >= sizeof(ushort) && *(ushort*)buffer.Value != 0;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }
}
