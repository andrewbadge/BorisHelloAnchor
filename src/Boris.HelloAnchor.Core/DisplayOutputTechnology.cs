// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Core;

/// <summary>
/// Classifies <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> values (SPEC §7.4).
/// </summary>
public static class DisplayOutputTechnology
{
    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS</c> — common on older laptop panels.</summary>
    public const uint Lvds = 6;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED</c> — eDP, used by most modern laptops.</summary>
    public const uint DisplayPortEmbedded = 11;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED</c> — embedded UDI.</summary>
    public const uint UdiEmbedded = 13;

    /// <summary><c>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL</c> — generic "internal" connection.</summary>
    public const uint Internal = 0x80000000;

    /// <summary>
    /// Returns <see langword="true"/> if the output technology indicates a built-in laptop panel.
    /// </summary>
    /// <param name="outputTechnology">The raw <c>targetInfo.outputTechnology</c> value.</param>
    /// <remarks>
    /// Many laptops report their panel as eDP rather than <see cref="Internal"/>, so all four values are
    /// accepted. External LVDS connections are vanishingly rare, so accepting it costs nothing in practice.
    /// </remarks>
    public static bool IsInternal(uint outputTechnology) => outputTechnology is
        Internal or DisplayPortEmbedded or UdiEmbedded or Lvds;
}
