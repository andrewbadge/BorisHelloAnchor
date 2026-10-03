// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Core.Displays;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for choosing the target monitor and the built-in fallback (SPEC §7.4).</summary>
public sealed class TargetDisplaySelectorTests
{
    private static readonly DisplayMonitor Laptop = Monitor(@"\\.\DISPLAY1", "BOE0868", isInternal: true, isPrimary: false);
    private static readonly DisplayMonitor DellA = Monitor(@"\\.\DISPLAY2", "DEL41B8-AAA111", isInternal: false, isPrimary: true);
    private static readonly DisplayMonitor DellB = Monitor(@"\\.\DISPLAY3", "DEL41B8-BBB222", isInternal: false, isPrimary: false);

    /// <summary>Docked with two identical monitors.</summary>
    private static readonly DisplayMonitor[] Docked = [Laptop, DellA, DellB];

    /// <summary>Internal picks the built-in panel and isn't a fallback.</summary>
    [Fact]
    public void Internal_PicksBuiltIn()
    {
        var selection = TargetDisplaySelector.Select(Docked, HelloAnchorOptions.Default);

        Assert.Same(Laptop, selection?.Monitor);
        Assert.False(selection?.IsFallback);
    }

    /// <summary>Primary picks the main display.</summary>
    [Fact]
    public void Primary_PicksMainDisplay()
    {
        var selection = TargetDisplaySelector.Select(Docked, Options(TargetDisplayMode.Primary));

        Assert.Same(DellA, selection?.Monitor);
    }

    /// <summary>The serial distinguishes two monitors of the same model, wherever they sit in the list.</summary>
    [Fact]
    public void Monitor_ExactMatchWinsOverSameModel()
    {
        var selection = TargetDisplaySelector.Select(Docked, Options(TargetDisplayMode.Monitor, monitorId: "DEL41B8-BBB222"));

        Assert.Same(DellB, selection?.Monitor);
        Assert.False(selection?.IsFallback);
    }

    /// <summary>A model-only ID matches the first monitor of that model.</summary>
    [Fact]
    public void Monitor_ModelOnlyIdMatchesFirstOfModel()
    {
        var selection = TargetDisplaySelector.Select(Docked, Options(TargetDisplayMode.Monitor, monitorId: "DEL41B8"));

        Assert.Same(DellA, selection?.Monitor);
    }

    /// <summary>Undocked, the chosen monitor is missing, so the built-in panel is used and flagged as a fallback.</summary>
    [Fact]
    public void Monitor_NotAttached_FallsBackToBuiltIn()
    {
        var selection = TargetDisplaySelector.Select([Laptop], Options(TargetDisplayMode.Monitor, monitorId: "DEL41B8-BBB222"));

        Assert.Same(Laptop, selection?.Monitor);
        Assert.True(selection?.IsFallback);
    }

    /// <summary>A different serial of the same model is not the chosen monitor.</summary>
    [Fact]
    public void Monitor_DifferentSerial_FallsBackToBuiltIn()
    {
        var selection = TargetDisplaySelector.Select([Laptop, DellA], Options(TargetDisplayMode.Monitor, monitorId: "DEL41B8-BBB222"));

        Assert.Same(Laptop, selection?.Monitor);
        Assert.True(selection?.IsFallback);
    }

    /// <summary>A GDI name that isn't attached also falls back to the built-in panel.</summary>
    [Fact]
    public void DeviceName_NotAttached_FallsBackToBuiltIn()
    {
        var hit = TargetDisplaySelector.Select(Docked, Options(TargetDisplayMode.DeviceName, deviceName: @"\\.\display3"));
        var miss = TargetDisplaySelector.Select(Docked, Options(TargetDisplayMode.DeviceName, deviceName: @"\\.\DISPLAY9"));

        Assert.Same(DellB, hit?.Monitor);
        Assert.Same(Laptop, miss?.Monitor);
        Assert.True(miss?.IsFallback);
    }

    /// <summary>Lid closed and the chosen monitor unplugged: nothing to move to.</summary>
    [Fact]
    public void NoTargetAndNoBuiltIn_GivesNull()
    {
        Assert.Null(TargetDisplaySelector.Select([DellA], Options(TargetDisplayMode.Monitor, monitorId: "DEL41B8-BBB222")));
        Assert.Null(TargetDisplaySelector.Select([DellA], HelloAnchorOptions.Default));
    }

    /// <summary>Monitors without an ID never match.</summary>
    [Fact]
    public void MonitorWithoutId_NeverMatches()
    {
        var unknown = Laptop with { MonitorId = null };

        Assert.Null(TargetDisplaySelector.FindMonitor([unknown], "BOE0868"));
    }

    /// <summary>Home and work each get their own choice; an unknown setup uses the top-level setting.</summary>
    [Fact]
    public void Profiles_PickChoiceForAttachedSetup()
    {
        var homeScreen = Monitor(@"\\.\DISPLAY2", "GSM5B7F-HOME1", isInternal: false, isPrimary: true);
        var options = Options(TargetDisplayMode.Internal) with
        {
            Profiles =
            [
                new DisplayProfile(["BOE0868", "GSM5B7F-HOME1"], TargetDisplayMode.Monitor, "GSM5B7F-HOME1"),
                new DisplayProfile(["BOE0868", "DEL41B8-AAA111", "DEL41B8-BBB222"], TargetDisplayMode.Monitor, "DEL41B8-BBB222"),
            ],
        };

        var home = TargetDisplaySelector.Select([homeScreen, Laptop], options);
        var work = TargetDisplaySelector.Select([DellB, Laptop, DellA], options); // Order doesn't matter.
        var elsewhere = TargetDisplaySelector.Select([Laptop, DellA], options);

        Assert.Same(homeScreen, home?.Monitor);
        Assert.Same(options.Profiles[0], home?.Profile);
        Assert.Same(DellB, work?.Monitor);
        Assert.Same(Laptop, elsewhere?.Monitor);
        Assert.Null(elsewhere?.Profile);
    }

    private static HelloAnchorOptions Options(TargetDisplayMode mode, string? monitorId = null, string? deviceName = null) =>
        HelloAnchorOptions.Default with { TargetDisplay = mode, TargetMonitorId = monitorId, TargetDeviceName = deviceName };

    private static DisplayMonitor Monitor(string deviceName, string monitorId, bool isInternal, bool isPrimary)
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        return new DisplayMonitor(deviceName, monitorId, monitorId, isInternal, isPrimary, bounds, bounds);
    }
}
