// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Core.Logging;
using Boris.HelloAnchor.Service;
using Boris.HelloAnchor.Service.Agents;
using Boris.HelloAnchor.Service.Security;
using Boris.HelloAnchor.Service.Sessions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Serilog.Extensions.Logging;

// Entry point for Boris.HelloAnchor.Service (SPEC §6). Runs as LocalSystem under the SCM; can also be started
// from a console for development (session-change events and some privileges are then unavailable).

var runningAsService = WindowsServiceHelpers.IsWindowsService();

// Harden %ProgramData%\Boris\HelloAnchor before anything reads config or opens a log file there (SPEC §8.2).
// Only meaningful (and only possible) as SYSTEM.
var hardeningMessages = runningAsService ? DataFolderSecurity.Enforce() : [];

var builder = Host.CreateApplicationBuilder(args);

// Windows service integration.
builder.Services.AddWindowsService(o => o.ServiceName = HelloAnchorNames.ServiceName);
if (runningAsService)
{
    // Must come after AddWindowsService so it replaces the default lifetime (SPEC §6.2). Registering it when
    // not under the SCM would make ServiceBase.Run fail, so it is conditional (SPEC §6.1).
    builder.Services.AddSingleton<IHostLifetime, SessionAwareServiceLifetime>();
}

// Logging: Serilog file sink (level switch driven by config) + Windows Event Log for lifecycle and problems.
var levelSwitch = HelloAnchorLogging.CreateLevelSwitch(LogLevel.Information);
var serilog = HelloAnchorLogging.CreateFileLogger(HelloAnchorLogging.ServiceLogFileName, levelSwitch);
builder.Logging.SetMinimumLevel(LogLevel.Trace); // Serilog's switch does the real filtering for the file.
builder.Logging.AddProvider(new SerilogLoggerProvider(serilog, dispose: true));
builder.Services.Configure<EventLogSettings>(s =>
{
    s.SourceName = HelloAnchorNames.EventLogSource;
    s.LogName = "Application";
});
builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Warning);
builder.Logging.AddFilter<EventLogLoggerProvider>(HelloAnchorNames.LifecycleLogCategory, LogLevel.Information);

// Components.
builder.Services.AddSingleton(sp => new ConfigWatcher(
    HelloAnchorPaths.ConfigFile,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigWatcher>()));
builder.Services.AddSingleton<SessionEventQueue>();
builder.Services.AddSingleton<ShutdownSignal>();
builder.Services.AddSingleton<AgentJob>();
builder.Services.AddSingleton<AgentLauncher>();
builder.Services.AddHostedService<SessionManager>();

using var host = builder.Build();

// Wire the configured log level (initial value and every reload) into Serilog.
var config = host.Services.GetRequiredService<ConfigWatcher>();
levelSwitch.MinimumLevel = HelloAnchorLogging.ToSerilogLevel(config.Current.LogLevel);
config.Changed += (_, options) => levelSwitch.MinimumLevel = HelloAnchorLogging.ToSerilogLevel(options.LogLevel);

// Report what the folder hardening did now that logging exists.
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Boris.HelloAnchor.Service.Security");
foreach (var message in hardeningMessages)
{
    startupLogger.LogWarning("Data folder hardening: {Message}", message);
}

if (!runningAsService)
{
    startupLogger.LogWarning("Not running under the Service Control Manager: session-change events are unavailable; relying on 30 s reconciliation.");
}

await host.RunAsync();
