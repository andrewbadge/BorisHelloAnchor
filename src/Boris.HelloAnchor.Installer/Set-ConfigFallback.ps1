# Copyright (C) 2026 Boris HelloAnchor contributors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free Software Foundation, either
# version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

<#
.SYNOPSIS
    Installer helper: writes AllowSystemTokenFallback into config.json.

.DESCRIPTION
    Run by the MSI (deferred custom action, via WixQuietExec) with the value of the public property
    ALLOWSYSTEMTOKENFALLBACK. Only the "AllowSystemTokenFallback" value is changed; everything else in the
    file, including comments and formatting, is left exactly as it is. The file is rewritten in place, so its
    owner and ACL are preserved.

    Exit codes: 0 = written or already correct; 1 = file missing or unrecognisable (the install fails, so a
    requested setting is never silently ignored).

.PARAMETER Path
    Full path of config.json.

.PARAMETER Value
    0 or 1.
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [ValidateSet('0', '1')] [string] $Value
)

$ErrorActionPreference = 'Stop'
$json = if ($Value -eq '1') { 'true' } else { 'false' }

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Output "config.json not found at '$Path'."
    exit 1
}

$text = [System.IO.File]::ReadAllText($Path)

# Case 1: the setting is present - replace just its value.
$pattern = '("AllowSystemTokenFallback"\s*:\s*)(true|false)'
if ([regex]::IsMatch($text, $pattern, 'IgnoreCase')) {
    $updated = [regex]::Replace($text, $pattern, "`${1}$json", 'IgnoreCase')
}
else {
    # Case 2: the setting is absent - insert it as the first entry of the "HelloAnchor" object.
    $section = '("HelloAnchor"\s*:\s*\{)'
    if (-not [regex]::IsMatch($text, $section, 'IgnoreCase')) {
        Write-Output "No ""HelloAnchor"" section in '$Path'; not changing it."
        exit 1
    }
    $updated = [regex]::Replace($text, $section, "`${1}`r`n    ""AllowSystemTokenFallback"": $json,", 'IgnoreCase')
}

if ($updated -ceq $text) {
    Write-Output "AllowSystemTokenFallback already $json."
    exit 0
}

# UTF-8 without BOM, matching the file the installer ships.
[System.IO.File]::WriteAllText($Path, $updated, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "AllowSystemTokenFallback set to $json in '$Path'."
exit 0
