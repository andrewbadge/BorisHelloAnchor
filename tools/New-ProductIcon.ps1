# Copyright (C) 2026 Boris HelloAnchor contributors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free Software Foundation, either
# version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

<#
.SYNOPSIS
    Regenerates the product icon (src/Boris.HelloAnchor.Installer/HelloAnchor.ico).

.DESCRIPTION
    Draws a simple, original icon - a laptop screen with a centred prompt box and a camera dot - at
    16, 24, 32, 48 and 256 pixels, and packs the PNG renderings into a single .ico file (PNG-in-ICO is
    supported by Windows Vista and later). Kept as a script so the icon is reproducible and contains no
    third-party artwork.

.PARAMETER OutputPath
    Where to write the .ico file.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\Boris.HelloAnchor.Installer\HelloAnchor.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Renders one square frame of the icon and returns it as PNG bytes.
function New-IconFrame([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $s = $size / 16.0

        # Screen bezel (dark) and panel (blue).
        $bezel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 38, 46))
        $panel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 103, 192))
        $g.FillRectangle($bezel, [float](0.5 * $s), [float](1.5 * $s), [float](15 * $s), [float](11 * $s))
        $g.FillRectangle($panel, [float](1.5 * $s), [float](3 * $s), [float](13 * $s), [float](8.5 * $s))

        # Laptop base.
        $g.FillRectangle($bezel, [float](0), [float](12.5 * $s), [float](16 * $s), [float](1.5 * $s))

        # Camera dot in the top bezel.
        $camera = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 120, 220, 120))
        $g.FillEllipse($camera, [float](7.4 * $s), [float](1.9 * $s), [float](1.2 * $s), [float](0.9 * $s))

        # The prompt, centred on the panel.
        $prompt = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.FillRectangle($prompt, [float](4.5 * $s), [float](5 * $s), [float](7 * $s), [float](4.5 * $s))

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $g.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = 16, 24, 32, 48, 256
$frames = foreach ($size in $sizes) { ,(New-IconFrame $size) }

# ICO layout: ICONDIR (6 bytes), one ICONDIRENTRY (16 bytes) per frame, then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type: icon
$writer.Write([uint16]$sizes.Count)   # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }  # 0 means 256
    $writer.Write([byte]$dimension)   # width
    $writer.Write([byte]$dimension)   # height
    $writer.Write([byte]0)            # palette colours
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # colour planes
    $writer.Write([uint16]32)         # bits per pixel
    $writer.Write([uint32]$frames[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}

foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()

$fullPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.File]::WriteAllBytes($fullPath, $out.ToArray())
Write-Host "Wrote $fullPath ($($out.Length) bytes)"
