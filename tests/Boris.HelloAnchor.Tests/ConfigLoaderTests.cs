// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for <see cref="ConfigLoader"/> (SPEC §4, §10).</summary>
public sealed class ConfigLoaderTests
{
    /// <summary>The documented example file must produce exactly the defaults, with no warnings.</summary>
    [Fact]
    public void FullDefaultFile_ParsesToDefaults()
    {
        const string json = """
            {
              "HelloAnchor": {
                "TargetDisplay": "Internal",
                "TargetDeviceName": null,
                "TargetProcessNames": [ "CredentialUIBroker" ],
                "TargetWindowClasses": [ "Credential Dialog Xaml Host" ],
                "VerifyDelaysMs": [ 150, 300, 600, 1000, 2000 ],
                "SkipRemoteSessions": true,
                "AllowSystemTokenFallback": false,
                "AgentRestartBackoffSeconds": [ 2, 5, 15, 60 ],
                "LogLevel": "Information"
              }
            }
            """;

        var result = ConfigLoader.Parse(json);

        Assert.Empty(result.Warnings);
        AssertEquivalent(HelloAnchorOptions.Default, result.Options);
    }

    /// <summary>Only the fields present are changed; everything else keeps its default.</summary>
    [Fact]
    public void PartialFile_OverridesOnlyGivenFields()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "TargetDisplay": "primary", "LogLevel": "Debug" } }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(TargetDisplayMode.Primary, result.Options.TargetDisplay);
        Assert.Equal(LogLevel.Debug, result.Options.LogLevel);
        Assert.Equal(HelloAnchorOptions.Default.VerifyDelaysMs, result.Options.VerifyDelaysMs);
    }

    /// <summary>A bound array replaces the default rather than being appended to it (the binder pitfall).</summary>
    [Fact]
    public void Arrays_ReplaceDefaults()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "VerifyDelaysMs": [ 100 ], "AgentRestartBackoffSeconds": [ 10 ] } }""");

        Assert.Equal([100], result.Options.VerifyDelaysMs);
        Assert.Equal([10], result.Options.AgentRestartBackoffSeconds);
    }

    /// <summary>Verify delays are sorted so the last check is always the latest one.</summary>
    [Fact]
    public void VerifyDelays_AreSorted()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "VerifyDelaysMs": [ 600, 150, 300 ] } }""");

        Assert.Equal([150, 300, 600], result.Options.VerifyDelaysMs);
    }

    /// <summary>Each invalid field falls back to its own default and produces one warning.</summary>
    [Theory]
    [InlineData("""{ "HelloAnchor": { "TargetDisplay": "Sideways" } }""")]
    [InlineData("""{ "HelloAnchor": { "TargetDisplay": "1" } }""")]
    [InlineData("""{ "HelloAnchor": { "TargetProcessNames": [] } }""")]
    [InlineData("""{ "HelloAnchor": { "TargetWindowClasses": [ "" ] } }""")]
    [InlineData("""{ "HelloAnchor": { "VerifyDelaysMs": [ -1 ] } }""")]
    [InlineData("""{ "HelloAnchor": { "VerifyDelaysMs": [ 20000 ] } }""")]
    [InlineData("""{ "HelloAnchor": { "VerifyDelaysMs": "150" } }""")]
    [InlineData("""{ "HelloAnchor": { "AgentRestartBackoffSeconds": [ 0 ] } }""")]
    [InlineData("""{ "HelloAnchor": { "AgentRestartBackoffSeconds": [ 4000 ] } }""")]
    [InlineData("""{ "HelloAnchor": { "SkipRemoteSessions": "yes" } }""")]
    [InlineData("""{ "HelloAnchor": { "AllowSystemTokenFallback": 1 } }""")]
    [InlineData("""{ "HelloAnchor": { "LogLevel": "Loud" } }""")]
    public void InvalidField_UsesDefaultAndWarns(string json)
    {
        var result = ConfigLoader.Parse(json);

        Assert.Single(result.Warnings);
        AssertEquivalent(HelloAnchorOptions.Default, result.Options);
    }

    /// <summary>One bad field doesn't discard the valid fields next to it.</summary>
    [Fact]
    public void InvalidField_DoesNotAffectOtherFields()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "LogLevel": "Loud", "TargetDisplay": "Primary" } }""");

        Assert.Single(result.Warnings);
        Assert.Equal(TargetDisplayMode.Primary, result.Options.TargetDisplay);
        Assert.Equal(LogLevel.Information, result.Options.LogLevel);
    }

    /// <summary>DeviceName mode without a device name reverts to Internal.</summary>
    [Fact]
    public void DeviceNameModeWithoutName_FallsBackToInternal()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "TargetDisplay": "DeviceName" } }""");

        Assert.Single(result.Warnings);
        Assert.Equal(TargetDisplayMode.Internal, result.Options.TargetDisplay);
    }

    /// <summary>DeviceName mode with a name is accepted as-is.</summary>
    [Fact]
    public void DeviceNameModeWithName_IsAccepted()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "TargetDisplay": "DeviceName", "TargetDeviceName": "\\\\.\\DISPLAY2" } }""");

        Assert.Empty(result.Warnings);
        Assert.Equal(TargetDisplayMode.DeviceName, result.Options.TargetDisplay);
        Assert.Equal(@"\\.\DISPLAY2", result.Options.TargetDeviceName);
    }

    /// <summary>A trailing ".exe" on a process name is tolerated.</summary>
    [Fact]
    public void ProcessNames_StripExeExtension()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "TargetProcessNames": [ "CredentialUIBroker.exe" ] } }""");

        Assert.Equal(["CredentialUIBroker"], result.Options.TargetProcessNames);
    }

    /// <summary>Hand-edited files often contain comments and trailing commas.</summary>
    [Fact]
    public void CommentsAndTrailingCommas_AreAllowed()
    {
        const string json = """
            {
              // user comment
              "HelloAnchor": { "LogLevel": "Warning", },
            }
            """;

        var result = ConfigLoader.Parse(json);

        Assert.Empty(result.Warnings);
        Assert.Equal(LogLevel.Warning, result.Options.LogLevel);
    }

    /// <summary>Unknown settings are ignored with a warning.</summary>
    [Fact]
    public void UnknownSetting_Warns()
    {
        var result = ConfigLoader.Parse("""{ "HelloAnchor": { "Colour": "Blue" } }""");

        Assert.Single(result.Warnings);
        AssertEquivalent(HelloAnchorOptions.Default, result.Options);
    }

    /// <summary>Malformed JSON and a missing section both yield defaults plus a warning.</summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "Other": {} }""")]
    [InlineData("""{ "HelloAnchor": 5 }""")]
    public void BadDocument_UsesDefaults(string json)
    {
        var result = ConfigLoader.Parse(json);

        Assert.Single(result.Warnings);
        AssertEquivalent(HelloAnchorOptions.Default, result.Options);
    }

    /// <summary>A missing file is not an error: defaults plus a warning.</summary>
    [Fact]
    public void MissingFile_UsesDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"helloanchor-missing-{Guid.NewGuid():N}.json");

        var result = ConfigLoader.LoadFile(path);

        Assert.Single(result.Warnings);
        AssertEquivalent(HelloAnchorOptions.Default, result.Options);
    }

    /// <summary>A real file on disk is read and parsed.</summary>
    [Fact]
    public void ExistingFile_IsLoaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"helloanchor-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "HelloAnchor": { "SkipRemoteSessions": false } }""");
        try
        {
            var result = ConfigLoader.LoadFile(path);

            Assert.Empty(result.Warnings);
            Assert.False(result.Options.SkipRemoteSessions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Oversized files are refused (the file is read by elevated code).</summary>
    [Fact]
    public void OversizedFile_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"helloanchor-big-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, new string(' ', ConfigLoader.MaxFileBytes + 1));
        try
        {
            var result = ConfigLoader.LoadFile(path);

            Assert.Single(result.Warnings);
            AssertEquivalent(HelloAnchorOptions.Default, result.Options);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Compares options by value (records compare list properties by reference).</summary>
    private static void AssertEquivalent(HelloAnchorOptions expected, HelloAnchorOptions actual)
    {
        Assert.Equal(expected.TargetDisplay, actual.TargetDisplay);
        Assert.Equal(expected.TargetDeviceName, actual.TargetDeviceName);
        Assert.Equal(expected.TargetProcessNames, actual.TargetProcessNames);
        Assert.Equal(expected.TargetWindowClasses, actual.TargetWindowClasses);
        Assert.Equal(expected.VerifyDelaysMs, actual.VerifyDelaysMs);
        Assert.Equal(expected.SkipRemoteSessions, actual.SkipRemoteSessions);
        Assert.Equal(expected.AllowSystemTokenFallback, actual.AllowSystemTokenFallback);
        Assert.Equal(expected.AgentRestartBackoffSeconds, actual.AgentRestartBackoffSeconds);
        Assert.Equal(expected.LogLevel, actual.LogLevel);
    }
}
