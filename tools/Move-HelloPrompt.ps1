# Copyright (C) 2026 Boris HelloAnchor contributors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free Software Foundation, either
# version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

<#
.SYNOPSIS
    Manual experiment that moves the Windows Hello / "Windows Security" prompt (SPEC section 1).

.DESCRIPTION
    Kept for reference: this is the experiment that established the product's design.

      1. Finds top-level windows of class "Credential Dialog Xaml Host" owned by CredentialUIBroker.exe.
      2. Calls SetWindowPos to centre each one on the chosen monitor's work area.
      3. Reports the Win32 error. From a normal PowerShell window the call fails with error 5
         (access denied, blocked by UIPI); from an elevated PowerShell window it succeeds.

    Trigger a prompt first (for example, register a passkey at https://webauthn.io with the camera
    covered), then run this script while the prompt is visible.

    Note: the original script was not in the repository when the project was scaffolded; this is a
    faithful re-creation of the steps described in the specification.

.PARAMETER MonitorIndex
    Zero-based index into the monitors reported by EnumDisplayMonitors. Defaults to the primary monitor.

.EXAMPLE
    .\Move-HelloPrompt.ps1
.EXAMPLE
    .\Move-HelloPrompt.ps1 -MonitorIndex 1
#>
[CmdletBinding()]
param(
    [int] $MonitorIndex = -1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Minimal user32 interop, compiled on the fly. The process is made per-monitor DPI aware first so every
# coordinate is in physical pixels, exactly as the agent does.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class HelloPromptInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    // Returns every visible top-level window with the given class name.
    public static List<IntPtr> FindWindowsByClass(string className)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(256);
            if (IsWindowVisible(hwnd) && GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == className)
            {
                result.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // Returns every monitor's info.
    public static List<MONITORINFOEX> GetMonitors()
    {
        var result = new List<MONITORINFOEX>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (GetMonitorInfo(h, ref info)) { result.Add(info); }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
[void][HelloPromptInterop]::SetProcessDpiAwarenessContext([IntPtr]-4)

$monitors = [HelloPromptInterop]::GetMonitors()
for ($i = 0; $i -lt $monitors.Count; $i++) {
    $m = $monitors[$i]
    $primary = if ($m.dwFlags -band 1) { ' (primary)' } else { '' }
    Write-Host ("[{0}] {1}{2} work area {3},{4}-{5},{6}" -f $i, $m.szDevice, $primary, $m.rcWork.Left, $m.rcWork.Top, $m.rcWork.Right, $m.rcWork.Bottom)
}

if ($MonitorIndex -lt 0) {
    $target = $monitors | Where-Object { $_.dwFlags -band 1 } | Select-Object -First 1
}
else {
    $target = $monitors[$MonitorIndex]
}

$windows = [HelloPromptInterop]::FindWindowsByClass('Credential Dialog Xaml Host') | Where-Object {
    $processId = 0
    [void][HelloPromptInterop]::GetWindowThreadProcessId($_, [ref]$processId)
    (Get-Process -Id $processId -ErrorAction SilentlyContinue).ProcessName -eq 'CredentialUIBroker'
}

if (-not $windows) {
    Write-Warning 'No credential prompt found. Trigger one first, then run this script while it is visible.'
    return
}

foreach ($hwnd in $windows) {
    $rect = New-Object HelloPromptInterop+RECT
    [void][HelloPromptInterop]::GetWindowRect($hwnd, [ref]$rect)
    $width  = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $x = $target.rcWork.Left + [math]::Max(0, [int](($target.rcWork.Right - $target.rcWork.Left - $width) / 2))
    $y = $target.rcWork.Top  + [math]::Max(0, [int](($target.rcWork.Bottom - $target.rcWork.Top - $height) / 2))

    # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    $ok = [HelloPromptInterop]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010)
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()

    if ($ok) {
        Write-Host ("Moved 0x{0:X} to ({1},{2}) on {3}." -f $hwnd.ToInt64(), $x, $y, $target.szDevice) -ForegroundColor Green
    }
    elseif ($err -eq 5) {
        Write-Host ("SetWindowPos failed with error 5 (access denied). Run this from an elevated PowerShell window.") -ForegroundColor Red
    }
    else {
        Write-Host ("SetWindowPos failed with Win32 error {0}." -f $err) -ForegroundColor Red
    }
}
