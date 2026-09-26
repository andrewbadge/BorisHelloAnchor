// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.


namespace Boris.HelloAnchor.Settings;

/// <summary>Entry point for the Settings app.</summary>
internal static class Program
{
    /// <summary>Starts the settings window.</summary>
    [STAThread]
    private static void Main()
    {
        // Applies ApplicationHighDpiMode and visual styles from the project file.
        ApplicationConfiguration.Initialize();
        Application.Run(new SettingsForm());
    }
}
