// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Boris.HelloAnchor.Core.Logging;

/// <summary>
/// Serilog setup shared by the service and the agent (SPEC §5): daily rolling files in
/// <see cref="HelloAnchorPaths.LogDirectory"/>, 14 files retained, level controlled at runtime.
/// </summary>
public static class HelloAnchorLogging
{
    /// <summary>Number of daily log files kept per log name.</summary>
    public const int RetainedFileCount = 14;

    /// <summary>Log line layout. Includes the source context so service/agent components are distinguishable.</summary>
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>File name pattern for the service log (Serilog inserts the date before the extension).</summary>
    public const string ServiceLogFileName = "service-.log";

    /// <summary>Returns the file name pattern for an agent running in <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Terminal Services session ID of the agent.</param>
    public static string AgentLogFileName(uint sessionId) => $"agent-s{sessionId}-.log";

    /// <summary>Creates a level switch initialised from a Microsoft.Extensions.Logging level.</summary>
    /// <param name="level">Initial minimum level.</param>
    public static LoggingLevelSwitch CreateLevelSwitch(LogLevel level) => new(ToSerilogLevel(level));

    /// <summary>
    /// Creates the Serilog file logger. Failure to create the log folder is not fatal: Serilog's file sink
    /// swallows write errors, so the process keeps running without file logs.
    /// </summary>
    /// <param name="fileName">File name pattern, e.g. <see cref="ServiceLogFileName"/>.</param>
    /// <param name="levelSwitch">Switch that controls the minimum level at runtime.</param>
    public static Serilog.Core.Logger CreateFileLogger(string fileName, LoggingLevelSwitch levelSwitch)
    {
        try
        {
            Directory.CreateDirectory(HelloAnchorPaths.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to log to yet; the sink will fail quietly too. Keep going.
        }

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(HelloAnchorPaths.LogDirectory, fileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                outputTemplate: OutputTemplate,
                // Shared: a short-lived duplicate agent (which exits with code 2) appends to the same file
                // instead of rolling over to a new "_001" file.
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    /// <summary>Maps a Microsoft.Extensions.Logging level to the equivalent Serilog level.</summary>
    /// <param name="level">The level to convert.</param>
    /// <remarks><see cref="LogLevel.None"/> maps to one above <see cref="LogEventLevel.Fatal"/>, which disables output.</remarks>
    public static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Fatal + 1,
    };
}
