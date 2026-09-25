// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for <see cref="WindowPlacement"/> centring maths (SPEC §7.5, §10).</summary>
public sealed class WindowPlacementTests
{
    /// <summary>A 1920×1080 primary monitor's work area with a 48 px taskbar at the bottom.</summary>
    private static readonly PixelRect Work = new(0, 0, 1920, 1032);

    /// <summary>A window smaller than the work area is centred on both axes.</summary>
    [Fact]
    public void CentreIn_SmallWindow_IsCentred()
    {
        var (x, y) = WindowPlacement.CentreIn(Work, 400, 300);

        Assert.Equal(760, x);
        Assert.Equal(366, y);
    }

    /// <summary>A window larger than the work area is clamped to the work area's top-left.</summary>
    [Fact]
    public void CentreIn_WindowLargerThanWorkArea_ClampsToTopLeft()
    {
        var (x, y) = WindowPlacement.CentreIn(Work, 2500, 1500);

        Assert.Equal(Work.Left, x);
        Assert.Equal(Work.Top, y);
    }

    /// <summary>Clamping is per axis: wider-but-shorter is clamped horizontally and centred vertically.</summary>
    [Fact]
    public void CentreIn_WiderOnly_ClampsHorizontallyOnly()
    {
        var (x, y) = WindowPlacement.CentreIn(Work, 2500, 300);

        Assert.Equal(0, x);
        Assert.Equal(366, y);
    }

    /// <summary>A monitor to the left of the primary has negative X coordinates.</summary>
    [Fact]
    public void CentreIn_MonitorLeftOfPrimary_HandlesNegativeCoordinates()
    {
        var left = new PixelRect(-2560, 0, 0, 1400);

        var (x, y) = WindowPlacement.CentreIn(left, 400, 300);

        Assert.Equal(-1480, x);
        Assert.Equal(550, y);
    }

    /// <summary>A monitor above the primary has negative Y coordinates.</summary>
    [Fact]
    public void CentreIn_MonitorAbovePrimary_HandlesNegativeCoordinates()
    {
        var above = new PixelRect(0, -1080, 1920, 0);

        var (x, y) = WindowPlacement.CentreIn(above, 400, 300);

        Assert.Equal(760, x);
        Assert.Equal(-690, y);
    }

    /// <summary>Centre-inside is true on the target and false on another monitor.</summary>
    [Fact]
    public void IsCentreInside_DetectsWhichMonitor()
    {
        var onTarget = new PixelRect(760, 366, 1160, 666);
        var elsewhere = new PixelRect(2680, 366, 3080, 666);

        Assert.True(WindowPlacement.IsCentreInside(onTarget, Work));
        Assert.False(WindowPlacement.IsCentreInside(elsewhere, Work));
    }

    /// <summary>A window straddling two monitors belongs to the one containing its centre.</summary>
    [Fact]
    public void IsCentreInside_StraddlingWindow_UsesCentre()
    {
        // Spans x = 1800..2200; centre 2000 is on the monitor to the right.
        var straddling = new PixelRect(1800, 100, 2200, 400);

        Assert.False(WindowPlacement.IsCentreInside(straddling, Work));
    }

    /// <summary>Right/bottom edges are exclusive, like Win32 RECT.</summary>
    [Fact]
    public void Contains_RightAndBottomAreExclusive()
    {
        Assert.True(Work.Contains(0, 0));
        Assert.False(Work.Contains(1920, 0));
        Assert.False(Work.Contains(0, 1032));
    }

    /// <summary>Up to ±2 px off-centre still counts as centred; 3 px does not.</summary>
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(2, -2, true)]
    [InlineData(-2, 2, true)]
    [InlineData(3, 0, false)]
    [InlineData(0, -3, false)]
    public void IsCentred_UsesTolerance(int dx, int dy, bool expected)
    {
        var window = new PixelRect(760 + dx, 366 + dy, 1160 + dx, 666 + dy);

        Assert.Equal(expected, WindowPlacement.IsCentred(window, Work));
    }

    /// <summary>After a DPI-driven resize the old position is no longer centred.</summary>
    [Fact]
    public void IsCentred_AfterResize_IsFalse()
    {
        // Positioned for 400×300 but now 600×450 (150 % scaling).
        var resized = new PixelRect(760, 366, 1360, 816);

        Assert.False(WindowPlacement.IsCentred(resized, Work));
    }
}
