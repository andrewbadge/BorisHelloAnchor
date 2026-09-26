// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Text;
using Boris.HelloAnchor.Core.Displays;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for stable monitor IDs (SPEC §7.4).</summary>
public sealed class MonitorIdentityTests
{
    /// <summary>A real-world shaped device path.</summary>
    private const string DevicePath = @"\\?\DISPLAY#DEL41B8#5&2a8c1b0&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>Manufacturer, product, both serial forms and the name are read from the EDID.</summary>
    [Fact]
    public void ParseEdid_ReadsIdentity()
    {
        var edid = MonitorIdentity.ParseEdid(BuildEdid(serialText: "5KC0Q83", name: "DELL U2723QE"));

        Assert.NotNull(edid);
        Assert.Equal("DEL", edid.ManufacturerId);
        Assert.Equal(0x41B8, edid.ProductCode);
        Assert.Equal(0x12345678u, edid.SerialNumber);
        Assert.Equal("5KC0Q83", edid.SerialText);
        Assert.Equal("DELL U2723QE", edid.Name);
        Assert.Equal("DEL41B8", edid.ModelId);
        Assert.Equal("DEL41B8-5KC0Q83", edid.MonitorId);
    }

    /// <summary>Without a serial descriptor, the numeric serial is used; without either, just the model.</summary>
    [Fact]
    public void MonitorId_FallsBackToNumericSerialThenModel()
    {
        Assert.Equal("DEL41B8-12345678", MonitorIdentity.ParseEdid(BuildEdid(serialText: null, name: null))!.MonitorId);
        Assert.Equal("DEL41B8", MonitorIdentity.ParseEdid(BuildEdid(serialText: null, name: null, serial: 0))!.MonitorId);
    }

    /// <summary>Spaces inside a serial descriptor are dropped so the ID is always valid configuration.</summary>
    [Fact]
    public void MonitorId_CleansSerialText()
    {
        var id = MonitorIdentity.ParseEdid(BuildEdid(serialText: "AB 12\"3", name: null))!.MonitorId;

        Assert.Equal("DEL41B8-AB123", id);
        Assert.True(MonitorIdentity.TryNormalise(id, out _));
    }

    /// <summary>Anything that isn't an EDID is rejected.</summary>
    [Fact]
    public void ParseEdid_RejectsInvalidData()
    {
        Assert.Null(MonitorIdentity.ParseEdid([]));
        Assert.Null(MonitorIdentity.ParseEdid(new byte[128]));

        var badLetters = BuildEdid(serialText: null, name: null);
        badLetters[8] = 0;
        badLetters[9] = 0;
        Assert.Null(MonitorIdentity.ParseEdid(badLetters));
    }

    /// <summary>The model and the registry instance ID come out of the device path.</summary>
    [Fact]
    public void DevicePath_GivesModelAndInstanceId()
    {
        Assert.Equal("DEL41B8", MonitorIdentity.ModelFromDevicePath(DevicePath));
        Assert.Equal(@"DISPLAY\DEL41B8\5&2a8c1b0&0&UID4352", MonitorIdentity.InstanceIdFromDevicePath(DevicePath));
    }

    /// <summary>Malformed paths, including ones that could escape the registry key, give nothing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"DISPLAY#DEL41B8#1#{x}")]
    [InlineData(@"\\?\DISPLAY#DEL41B8")]
    [InlineData(@"\\?\DISPLAY#..#..#{x}")]
    [InlineData(@"\\?\DISPLAY#DEL41B8#a\b#{x}")]
    public void DevicePath_Malformed_GivesNull(string? path)
    {
        Assert.Null(MonitorIdentity.InstanceIdFromDevicePath(path));
    }

    /// <summary>Valid IDs are accepted and the model part is upper-cased.</summary>
    [Theory]
    [InlineData("DEL41B8", "DEL41B8")]
    [InlineData("del41b8", "DEL41B8")]
    [InlineData(" DEL41B8-5KC0Q83 ", "DEL41B8-5KC0Q83")]
    [InlineData("BOE0868-abc-01", "BOE0868-abc-01")]
    public void TryNormalise_AcceptsValidIds(string input, string expected)
    {
        Assert.True(MonitorIdentity.TryNormalise(input, out var id));
        Assert.Equal(expected, id);
    }

    /// <summary>Invalid IDs are rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DEL41B")]
    [InlineData("DE141B8")]
    [InlineData("DEL41BZ")]
    [InlineData("DEL41B8-")]
    [InlineData("DEL41B8_ABC")]
    [InlineData("DEL41B8-A B")]
    [InlineData(@"\\.\DISPLAY1")]
    public void TryNormalise_RejectsInvalidIds(string? input)
    {
        Assert.False(MonitorIdentity.TryNormalise(input, out _));
    }

    /// <summary>Exact beats model; a missing serial on either side is a model match; different serials never match.</summary>
    [Theory]
    [InlineData("DEL41B8-5KC0Q83", "DEL41B8-5KC0Q83", MonitorMatch.Exact)]
    [InlineData("DEL41B8-5kc0q83", "DEL41B8-5KC0Q83", MonitorMatch.Exact)]
    [InlineData("DEL41B8", "DEL41B8", MonitorMatch.Exact)]
    [InlineData("DEL41B8-5KC0Q83", "DEL41B8", MonitorMatch.Model)]
    [InlineData("DEL41B8", "DEL41B8-5KC0Q83", MonitorMatch.Model)]
    [InlineData("DEL41B8-OTHER", "DEL41B8-5KC0Q83", MonitorMatch.None)]
    [InlineData("DEL41B9-5KC0Q83", "DEL41B8-5KC0Q83", MonitorMatch.None)]
    [InlineData(null, "DEL41B8", MonitorMatch.None)]
    public void Match_Classifies(string? monitorId, string configuredId, MonitorMatch expected)
    {
        Assert.Equal(expected, MonitorIdentity.Match(monitorId, configuredId));
    }

    /// <summary>Builds a 128-byte EDID base block for a Dell (DEL, product 0x41B8).</summary>
    private static byte[] BuildEdid(string? serialText, string? name, uint serial = 0x12345678)
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);

        // "DEL": D=4, E=5, L=12 → 0b0_00100_00101_01100 = 0x10AC, big-endian.
        edid[8] = 0x10;
        edid[9] = 0xAC;
        edid[10] = 0xB8;
        edid[11] = 0x41;
        BitConverter.GetBytes(serial).CopyTo(edid, 12);

        // The first descriptor is normally a detailed timing; leave it non-zero so it's skipped.
        edid[54] = 0x01;
        WriteDescriptor(edid, 72, 0xFF, serialText);
        WriteDescriptor(edid, 90, 0xFC, name);
        return edid;
    }

    /// <summary>Writes a display descriptor (00 00 00 tag 00 text…) if <paramref name="text"/> is set.</summary>
    private static void WriteDescriptor(byte[] edid, int offset, byte tag, string? text)
    {
        if (text is null)
        {
            edid[offset + 3] = 0x10; // Dummy descriptor.
            return;
        }

        edid[offset + 3] = tag;
        var field = Enumerable.Repeat((byte)0x20, 13).ToArray();
        var bytes = Encoding.ASCII.GetBytes(text + "\n");
        Array.Copy(bytes, field, Math.Min(bytes.Length, 13));
        field.CopyTo(edid, offset + 5);
    }
}
