// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Text.Json.Nodes;
using Boris.HelloAnchor.Core.Configuration;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for <see cref="ConfigWriter"/>, used by the Settings app.</summary>
public sealed class ConfigWriterTests
{
    /// <summary>The shipped default file.</summary>
    private const string ShippedConfig = """
        {
          "HelloAnchor": {
            "TargetDisplay": "Internal",
            "TargetDeviceName": null,
            "TargetProcessNames": [ "CredentialUIBroker" ],
            "AllowSystemTokenFallback": true,
            "LogLevel": "Debug"
          }
        }
        """;

    /// <summary>Choosing a monitor sets the mode and ID and leaves every other setting alone.</summary>
    [Fact]
    public void SetMonitor_KeepsOtherSettings()
    {
        var updated = ConfigWriter.SetTargetDisplay(ShippedConfig, TargetDisplayMode.Monitor, "DEL41B8-5KC0Q83");
        var result = ConfigLoader.Parse(updated);

        Assert.Empty(result.Warnings);
        Assert.Equal(TargetDisplayMode.Monitor, result.Options.TargetDisplay);
        Assert.Equal("DEL41B8-5KC0Q83", result.Options.TargetMonitorId);
        Assert.True(result.Options.AllowSystemTokenFallback);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, result.Options.LogLevel);
    }

    /// <summary>A new ID is placed right after TargetDeviceName, keeping the display settings together.</summary>
    [Fact]
    public void SetMonitor_InsertsIdAfterDeviceName()
    {
        var updated = ConfigWriter.SetTargetDisplay(ShippedConfig, TargetDisplayMode.Monitor, "DEL41B8");
        var keys = ((JsonObject)JsonNode.Parse(updated)!["HelloAnchor"]!).Select(p => p.Key).ToArray();

        Assert.Equal(["TargetDisplay", "TargetDeviceName", "TargetMonitorId", "TargetProcessNames", "AllowSystemTokenFallback", "LogLevel"], keys);
    }

    /// <summary>Existing keys keep their spelling and position, and are not duplicated.</summary>
    [Fact]
    public void SetMode_ReusesExistingKeysCaseInsensitively()
    {
        const string json = """{ "helloanchor": { "logLevel": "Warning", "targetdisplay": "Monitor", "targetMonitorId": "DEL41B8" } }""";

        var updated = ConfigWriter.SetTargetDisplay(json, TargetDisplayMode.Primary, monitorId: null);
        var section = (JsonObject)JsonNode.Parse(updated)!["helloanchor"]!;

        Assert.Equal(["logLevel", "targetdisplay", "targetMonitorId"], section.Select(p => p.Key).ToArray());
        Assert.Equal("Primary", (string?)section["targetdisplay"]);
        Assert.Equal("DEL41B8", (string?)section["targetMonitorId"]);
    }

    /// <summary>With no file, a minimal valid one is produced.</summary>
    [Fact]
    public void NoFile_CreatesSection()
    {
        var updated = ConfigWriter.SetTargetDisplay(null, TargetDisplayMode.Monitor, "DEL41B8");
        var result = ConfigLoader.Parse(updated);

        Assert.Empty(result.Warnings);
        Assert.Equal(TargetDisplayMode.Monitor, result.Options.TargetDisplay);
    }

    /// <summary>A broken file is refused rather than overwritten.</summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "HelloAnchor": 3 }""")]
    public void InvalidFile_Throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => ConfigWriter.SetTargetDisplay(json, TargetDisplayMode.Internal, null));
    }

    /// <summary>Comments are detected (so the app can warn that saving drops them).</summary>
    [Fact]
    public void HasComments_DetectsComments()
    {
        Assert.True(ConfigWriter.HasComments("""{ /* note */ "HelloAnchor": {} }"""));
        Assert.True(ConfigWriter.HasComments("{\n  // note\n  \"HelloAnchor\": {}\n}"));
        Assert.False(ConfigWriter.HasComments(ShippedConfig));
        Assert.False(ConfigWriter.HasComments("""{ "HelloAnchor": { "TargetDeviceName": "// not a comment" } }"""));
    }

    /// <summary>Each setup gets its own profile; saving the same setup again replaces it, whatever the monitor order.</summary>
    [Fact]
    public void SetProfile_AddsPerSetupAndReplacesSameSetup()
    {
        string[] home = ["BOE0868", "GSM5B7F-HOME1"];
        string[] work = ["DEL41B8-BBB222", "BOE0868", "DEL41B8-AAA111"];

        var json = ConfigWriter.SetProfile(ShippedConfig, home, TargetDisplayMode.Internal, null);
        json = ConfigWriter.SetProfile(json, work, TargetDisplayMode.Monitor, "DEL41B8-AAA111");
        json = ConfigWriter.SetProfile(json, [.. work.Reverse()], TargetDisplayMode.Monitor, "DEL41B8-BBB222");
        var result = ConfigLoader.Parse(json);

        Assert.Empty(result.Warnings);
        Assert.Equal(TargetDisplayMode.Internal, result.Options.TargetDisplay); // Top level untouched.
        Assert.Equal(2, result.Options.Profiles.Count);
        Assert.Equal(TargetDisplayMode.Internal, result.Options.Profiles[0].TargetDisplay);
        Assert.Null(result.Options.Profiles[0].TargetMonitorId);
        Assert.Equal(["BOE0868", "DEL41B8-AAA111", "DEL41B8-BBB222"], result.Options.Profiles[1].Monitors);
        Assert.Equal("DEL41B8-BBB222", result.Options.Profiles[1].TargetMonitorId);
    }

    /// <summary>Writing in place replaces the whole contents, including a longer old file.</summary>
    [Fact]
    public void WriteInPlace_TruncatesOldContents()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('x', 1000));
            ConfigWriter.WriteInPlace(path, "{}");

            Assert.Equal("{}", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
