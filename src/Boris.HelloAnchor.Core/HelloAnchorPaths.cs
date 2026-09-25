// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Core;

/// <summary>
/// Well-known file-system locations shared by the service and the agent (SPEC §5).
/// </summary>
/// <remarks>
/// These paths are deliberately not configurable (no environment-variable or command-line override):
/// both processes run with elevated rights, so the locations they trust must be fixed.
/// </remarks>
public static class HelloAnchorPaths
{
    /// <summary>
    /// <c>%ProgramData%\Boris\HelloAnchor</c> — holds <c>config.json</c> and the <c>logs</c> folder.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Boris",
        "HelloAnchor");

    /// <summary>
    /// <c>%ProgramData%\Boris</c> — the company folder that contains <see cref="DataDirectory"/>.
    /// </summary>
    public static string CompanyDataDirectory { get; } = Path.GetDirectoryName(DataDirectory)!;

    /// <summary>Full path of the shared configuration file.</summary>
    public static string ConfigFile { get; } = Path.Combine(DataDirectory, ConfigFileName);

    /// <summary>Folder that receives the rolling Serilog files.</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>File name of the configuration file (used as the <see cref="FileSystemWatcher"/> filter).</summary>
    public const string ConfigFileName = "config.json";
}
