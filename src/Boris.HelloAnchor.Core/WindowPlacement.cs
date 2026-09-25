// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Core;

/// <summary>
/// A rectangle in physical (per-monitor-DPI-aware) virtual-screen pixels. <see cref="Right"/> and
/// <see cref="Bottom"/> are exclusive, matching Win32 <c>RECT</c> semantics.
/// </summary>
/// <param name="Left">X of the left edge.</param>
/// <param name="Top">Y of the top edge.</param>
/// <param name="Right">X one past the right edge.</param>
/// <param name="Bottom">Y one past the bottom edge.</param>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels.</summary>
    public int Height => Bottom - Top;

    /// <summary>X of the centre point (rounded towards <see cref="Left"/>).</summary>
    public int CentreX => Left + (Width / 2);

    /// <summary>Y of the centre point (rounded towards <see cref="Top"/>).</summary>
    public int CentreY => Top + (Height / 2);

    /// <summary>Returns <see langword="true"/> if the point lies inside this rectangle.</summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <inheritdoc />
    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>
/// Pure placement maths used by the agent (SPEC §7.5). Kept free of Win32 so it can be unit-tested.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Default tolerance, in pixels, when deciding whether a window is "still centred". Prevents endless
    /// re-centring caused by DPI rounding after the window crosses monitors (SPEC §7.5).
    /// </summary>
    public const int CentredTolerancePx = 2;

    /// <summary>
    /// Computes the top-left position that centres a <paramref name="width"/> × <paramref name="height"/>
    /// window inside <paramref name="workArea"/>.
    /// </summary>
    /// <param name="workArea">Target monitor work area (<c>rcWork</c>).</param>
    /// <param name="width">Current window width.</param>
    /// <param name="height">Current window height.</param>
    /// <returns>The new top-left corner. On any axis where the window is larger than the work area, the
    /// window is aligned to the work area's top/left edge so its title and controls stay reachable.</returns>
    public static (int X, int Y) CentreIn(PixelRect workArea, int width, int height)
    {
        var x = width >= workArea.Width ? workArea.Left : workArea.Left + ((workArea.Width - width) / 2);
        var y = height >= workArea.Height ? workArea.Top : workArea.Top + ((workArea.Height - height) / 2);
        return (x, y);
    }

    /// <summary>Returns <see langword="true"/> if the window's centre point lies inside the work area.</summary>
    /// <param name="window">Current window rectangle.</param>
    /// <param name="workArea">Target monitor work area.</param>
    public static bool IsCentreInside(PixelRect window, PixelRect workArea) =>
        workArea.Contains(window.CentreX, window.CentreY);

    /// <summary>
    /// Returns <see langword="true"/> if the window's top-left corner is within <paramref name="tolerance"/>
    /// pixels of the centred position for its current size.
    /// </summary>
    /// <param name="window">Current window rectangle.</param>
    /// <param name="workArea">Target monitor work area.</param>
    /// <param name="tolerance">Allowed deviation in pixels on each axis.</param>
    public static bool IsCentred(PixelRect window, PixelRect workArea, int tolerance = CentredTolerancePx)
    {
        var (x, y) = CentreIn(workArea, window.Width, window.Height);
        return Math.Abs(window.Left - x) <= tolerance && Math.Abs(window.Top - y) <= tolerance;
    }
}
