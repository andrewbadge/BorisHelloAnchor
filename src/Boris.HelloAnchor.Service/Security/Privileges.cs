// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Security;

namespace Boris.HelloAnchor.Service.Security;

/// <summary>Enables privileges that LocalSystem holds but has disabled by default.</summary>
internal static class Privileges
{
    /// <summary>Allows taking ownership of any object regardless of its DACL.</summary>
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";

    /// <summary>Allows setting any owner and writing any DACL.</summary>
    public const string Restore = "SeRestorePrivilege";

    /// <summary>
    /// Enables <paramref name="privilegeName"/> in the current process token.
    /// </summary>
    /// <param name="privilegeName">A privilege name such as <see cref="TakeOwnership"/>.</param>
    /// <returns><see langword="true"/> if the privilege is now enabled.</returns>
    public static unsafe bool TryEnable(string privilegeName)
    {
        using var process = PInvoke.GetCurrentProcess_SafeHandle();
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!PInvoke.LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            privileges.Privileges[0] = new LUID_AND_ATTRIBUTES
            {
                Luid = luid,
                Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED,
            };

            // AdjustTokenPrivileges "succeeds" even when the token doesn't hold the privilege; the real answer
            // is ERROR_NOT_ALL_ASSIGNED (1300) in the last error.
            const int errorNotAllAssigned = 1300;
            return PInvoke.AdjustTokenPrivileges(token, false, &privileges, default) &&
                   Marshal.GetLastPInvokeError() != errorNotAllAssigned;
        }
    }
}
