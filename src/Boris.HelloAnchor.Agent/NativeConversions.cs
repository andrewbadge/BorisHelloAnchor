// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;
using Windows.Win32.Foundation;

namespace Boris.HelloAnchor.Agent;

/// <summary>Small conversions between CsWin32 structs and the Core types.</summary>
internal static class NativeConversions
{
    /// <summary>Converts a Win32 <see cref="RECT"/> to a <see cref="PixelRect"/>.</summary>
    /// <param name="rect">The native rectangle.</param>
    public static PixelRect ToPixelRect(this RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    /// <summary>Formats a window handle the way Spy++ and most tools display it.</summary>
    /// <param name="hwnd">The window handle.</param>
    public static unsafe string Format(this HWND hwnd) => $"0x{(nint)hwnd.Value:X8}";
}
