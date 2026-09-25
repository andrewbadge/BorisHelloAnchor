# Copyright (C) 2026 Boris HelloAnchor contributors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free Software Foundation, either
# version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

<#
.SYNOPSIS
    Sanity-checks a built Boris HelloAnchor MSI before it is uploaded or released.

.DESCRIPTION
    A cheap guard against shipping a package that built but is wrong. Opens the MSI database read-only and
    checks that:
      - ProductVersion matches the expected version;
      - UpgradeCode is the project's fixed code (a changed code would install side by side instead of
        upgrading);
      - the package is per-machine (ALLUSERS=1), which the LocalSystem service requires;
      - the Service and Agent executables, the licence and the third-party notices are in the payload;
      - the service is registered as Boris.HelloAnchor, auto-start, running as LocalSystem.
    Used by both the Build and Release workflows so the two can't drift apart.

.PARAMETER Path
    The MSI to check.

.PARAMETER ExpectedVersion
    The major.minor.build version the MSI must carry.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Must match Package.wxs. It never changes between versions.
$expectedUpgradeCode = '{5616EF1D-6C1C-42C6-8C9D-CDBA78CAD767}'

if (-not (Test-Path $Path)) { throw "MSI not found at $Path" }

$installer = New-Object -ComObject WindowsInstaller.Installer
# Open mode 0 = msiOpenDatabaseModeReadOnly.
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @((Resolve-Path $Path).Path, 0))

# Runs a query and returns each row as 'col1|col2|...'.
function Invoke-MsiQuery([string] $sql, [int] $columns) {
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rows = @()
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }
        $values = @()
        for ($c = 1; $c -le $columns; $c++) {
            $values += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($c))
        }
        $rows += , ($values -join '|')
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    return $rows
}

# Returns the value of an MSI property, or $null.
function Get-MsiProperty([string] $name) {
    $row = $properties | Where-Object { $_ -like "$name|*" } | Select-Object -First 1
    if ($row) { return $row.Substring($name.Length + 1) }
    return $null
}

$properties = Invoke-MsiQuery 'SELECT `Property`, `Value` FROM `Property`' 2

$version = Get-MsiProperty 'ProductVersion'
if ($version -ne $ExpectedVersion) { throw "ProductVersion is '$version', expected '$ExpectedVersion'." }

$upgradeCode = Get-MsiProperty 'UpgradeCode'
if ($upgradeCode -ne $expectedUpgradeCode) { throw "UpgradeCode is '$upgradeCode', expected '$expectedUpgradeCode'. Upgrades would break." }

# The service is installed machine-wide, so the package must be per-machine.
if ((Get-MsiProperty 'ALLUSERS') -ne '1') { throw 'ALLUSERS is not 1; the package is not per-machine.' }

# File table names are 'SHORT~1.EXE|LongName.exe'; match on the long name.
$files = Invoke-MsiQuery 'SELECT `FileName` FROM `File`' 1
foreach ($required in 'Boris.HelloAnchor.Service.exe', 'Boris.HelloAnchor.Agent.exe', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'config.json') {
    if (-not ($files | Where-Object { $_ -like "*$required" })) { throw "$required is missing from the MSI." }
}

# ServiceInstall: Name, StartType (2 = auto), StartName (empty or LocalSystem means LocalSystem).
$services = Invoke-MsiQuery 'SELECT `Name`, `StartType`, `StartName` FROM `ServiceInstall`' 3
$service = $services | Where-Object { $_ -like 'Boris.HelloAnchor|*' } | Select-Object -First 1
if (-not $service) { throw 'ServiceInstall entry for Boris.HelloAnchor is missing.' }
$name, $startType, $account = $service.Split('|')
if ($startType -ne '2') { throw "Service start type is $startType, expected 2 (automatic)." }
if ($account -and $account -ne 'LocalSystem') { throw "Service account is '$account', expected LocalSystem." }

Write-Host "MSI verified: version $version, $($files.Count) files, per-machine, service '$name' (auto, LocalSystem)."
