# Copyright (C) 2026 Boris HelloAnchor contributors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free Software Foundation, either
# version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

<#
.SYNOPSIS
    Builds, tests, publishes, optionally signs, and packages Boris HelloAnchor (SPEC section 9).

.DESCRIPTION
    1. Restores and builds the solution in Release (warnings are errors via Directory.Build.props).
    2. Runs the unit tests (unless -SkipTests).
    3. Publishes the Service, Agent and Settings app, self-contained, into artifacts\publish\win-x64\.
    4. Optionally signs the three executables.
    5. Builds the WiX installer -> artifacts\Boris.HelloAnchor-{version}-x64.msi.
    6. Optionally signs the MSI.
    7. Prints the MSI path.

    Signing happens only when -CertificateThumbprint or -PfxPath is given; otherwise it is skipped
    without failing the build.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER Version
    Overrides <Version> from Directory.Build.props for this build only (major.minor.build). Used by CI to
    stamp test builds; releases should change Directory.Build.props instead.

.PARAMETER SkipTests
    Skip the unit tests.

.PARAMETER CertificateThumbprint
    Thumbprint of a code-signing certificate in the current user's or local machine's certificate store.

.PARAMETER PfxPath
    Path to a .pfx code-signing certificate (alternative to -CertificateThumbprint).

.PARAMETER PfxPassword
    Password for -PfxPath, as a SecureString.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used when signing.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding(DefaultParameterSetName = 'Unsigned')]
param(
    [string] $Configuration = 'Release',
    [string] $Version,
    [switch] $SkipTests,

    [Parameter(ParameterSetName = 'Thumbprint', Mandatory)]
    [string] $CertificateThumbprint,

    [Parameter(ParameterSetName = 'Pfx', Mandatory)]
    [string] $PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [securestring] $PfxPassword,

    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = $PSScriptRoot
$solution   = Join-Path $repoRoot 'Boris.HelloAnchor.sln'
$artifacts  = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifacts 'publish\win-x64'
$installer  = Join-Path $repoRoot 'src\Boris.HelloAnchor.Installer\Boris.HelloAnchor.Installer.wixproj'
$tests      = Join-Path $repoRoot 'tests\Boris.HelloAnchor.Tests'
$signing    = $PSCmdlet.ParameterSetName -ne 'Unsigned'

# Runs a native command and stops the script if it fails.
function Invoke-Checked([string] $description, [scriptblock] $command) {
    Write-Host "==> $description" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$description failed with exit code $LASTEXITCODE."
    }
}

# Locates signtool.exe in the newest installed Windows SDK.
function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { $_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'signtool.exe not found. Install the Windows SDK signing tools or put signtool on PATH.'
    }
    return $candidate.FullName
}

# Signs one or more files with the certificate given on the command line.
function Invoke-Sign([string[]] $files) {
    $signTool = Find-SignTool
    $arguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')

    if ($PSCmdlet.ParameterSetName -eq 'Thumbprint') {
        $arguments += @('/sha1', $CertificateThumbprint)
    }
    else {
        $arguments += @('/f', (Resolve-Path $PfxPath).Path)
        if ($PfxPassword) {
            # signtool only accepts the password on the command line; convert only for this call.
            $plain = [System.Net.NetworkCredential]::new('', $PfxPassword).Password
            $arguments += @('/p', $plain)
        }
    }

    Invoke-Checked "Signing $($files.Count) file(s)" { & $signTool @arguments @files }
}

# The single shared version lives in Directory.Build.props.
if ($Version) {
    # An MSI ProductVersion is numeric major.minor.build; fail early rather than deep inside WiX.
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version '$Version' is not major.minor.build (e.g. 1.2.3)."
    }
    $version = $Version
}
else {
    $version = (& dotnet msbuild (Join-Path $repoRoot 'src\Boris.HelloAnchor.Core\Boris.HelloAnchor.Core.csproj') -getProperty:Version).Trim()
}

# Passed to every build/publish so assemblies and the MSI all carry the same version.
$versionProperty = "-p:Version=$version"
Write-Host "Boris HelloAnchor $version ($Configuration)" -ForegroundColor Green

# Start from a clean publish folder so stale files never end up in the MSI.
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Invoke-Checked 'Restore' { dotnet restore $solution }
Invoke-Checked 'Build' { dotnet build $solution -c $Configuration --no-restore $versionProperty }

if (-not $SkipTests) {
    Invoke-Checked 'Unit tests' { dotnet test --project $tests -c $Configuration --no-build }
}

foreach ($project in 'Boris.HelloAnchor.Service', 'Boris.HelloAnchor.Agent', 'Boris.HelloAnchor.Settings') {
    $path = Join-Path $repoRoot "src\$project\$project.csproj"
    Invoke-Checked "Publish $project" {
        dotnet publish $path -c $Configuration -r win-x64 --self-contained -o $publishDir -p:PublishSingleFile=false -p:PublishTrimmed=false $versionProperty
    }
}

if ($signing) {
    Invoke-Sign @(
        (Join-Path $publishDir 'Boris.HelloAnchor.Service.exe'),
        (Join-Path $publishDir 'Boris.HelloAnchor.Agent.exe'),
        (Join-Path $publishDir 'Boris.HelloAnchor.Settings.exe'))
}
else {
    Write-Host '==> No certificate supplied; skipping code signing.' -ForegroundColor Yellow
}

Invoke-Checked 'Build installer' { dotnet build $installer -c Release "-p:PublishDir=$publishDir\" $versionProperty }

$msi = Join-Path $artifacts "Boris.HelloAnchor-$version-x64.msi"
if (-not (Test-Path $msi)) {
    throw "Expected installer not found: $msi"
}

if ($signing) {
    Invoke-Sign @($msi)
}

Write-Host ''
Write-Host "Installer: $msi" -ForegroundColor Green
