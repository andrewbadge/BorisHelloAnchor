// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Boris.HelloAnchor.Settings;

/// <summary>Entry point for the Settings app.</summary>
internal static class Program
{
    /// <summary>File log for the Settings app (settings-.log).</summary>
    internal static Serilog.ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    /// <summary>Starts the settings window.</summary>
    [STAThread]
    private static void Main()
    {
        // Unexpected exceptions are logged, never shown as the WinForms crash dialog.
        using var log = HelloAnchorLogging.CreateFileLogger("settings-.log", HelloAnchorLogging.CreateLevelSwitch(LogLevel.Information));
        Log = log;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Error(e.Exception, "Unhandled exception in the Settings app.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Non-UI thread: the runtime still terminates, so flush first.
            log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; Settings app terminating.");
            log.Dispose();
        };

        try
        {
            // Applies ApplicationHighDpiMode and visual styles from the project file.
            ApplicationConfiguration.Initialize();
            Application.Run(new SettingsForm());
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Settings app failed.");
        }
    }
}
