// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for internal-panel detection (SPEC §7.4, §10).</summary>
public sealed class DisplayOutputTechnologyTests
{
    /// <summary>INTERNAL, eDP, embedded UDI and LVDS are all built-in panels.</summary>
    [Theory]
    [InlineData(0x80000000u)] // INTERNAL
    [InlineData(11u)]         // DISPLAYPORT_EMBEDDED
    [InlineData(13u)]         // UDI_EMBEDDED
    [InlineData(6u)]          // LVDS
    public void Internal_Technologies_AreInternal(uint technology)
    {
        Assert.True(DisplayOutputTechnology.IsInternal(technology));
    }

    /// <summary>External connectors are not internal.</summary>
    [Theory]
    [InlineData(0u)]          // HD15 (VGA)
    [InlineData(4u)]          // DVI
    [InlineData(5u)]          // HDMI
    [InlineData(10u)]         // DISPLAYPORT_EXTERNAL
    [InlineData(12u)]         // UDI_EXTERNAL
    [InlineData(16u)]         // MIRACAST
    [InlineData(0xFFFFFFFFu)] // OTHER
    public void External_Technologies_AreNotInternal(uint technology)
    {
        Assert.False(DisplayOutputTechnology.IsInternal(technology));
    }
}
