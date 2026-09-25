// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Boris.HelloAnchor.Core.Configuration;

/// <summary>The outcome of loading configuration: the effective options plus any problems found.</summary>
/// <param name="Options">Effective options. Always usable; invalid fields have been replaced by defaults.</param>
/// <param name="Warnings">Human-readable descriptions of every problem that caused a default to be used.</param>
public sealed record ConfigLoadResult(HelloAnchorOptions Options, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and validates <c>config.json</c> (SPEC §4).
/// </summary>
/// <remarks>
/// <para>
/// Parsing uses <see cref="JsonDocument"/> directly instead of <c>Microsoft.Extensions.Configuration</c>
/// binding, because the binder appends bound arrays to default arrays rather than replacing them.
/// </para>
/// <para>
/// Validation is per field: a bad value resets only that field to its default and records a warning.
/// Loading never throws; the worst case is <see cref="HelloAnchorOptions.Default"/> plus warnings.
/// </para>
/// </remarks>
public static class ConfigLoader
{
    /// <summary>Name of the root JSON object that holds the settings.</summary>
    public const string SectionName = "HelloAnchor";

    /// <summary>Largest file accepted. The file is read by elevated code, so refuse anything unreasonable.</summary>
    internal const int MaxFileBytes = 64 * 1024;

    /// <summary>Upper bound for each <see cref="HelloAnchorOptions.VerifyDelaysMs"/> entry.</summary>
    internal const int MaxVerifyDelayMs = 10_000;

    /// <summary>Upper bound for each <see cref="HelloAnchorOptions.AgentRestartBackoffSeconds"/> entry.</summary>
    internal const int MaxBackoffSeconds = 3_600;

    /// <summary>Lenient reader settings: comments and trailing commas are common in hand-edited files.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    /// <summary>
    /// Loads configuration from <paramref name="path"/> and logs any warnings to <paramref name="logger"/>.
    /// </summary>
    /// <param name="path">Path to <c>config.json</c>.</param>
    /// <param name="logger">Destination for validation warnings.</param>
    /// <returns>The effective options (never <see langword="null"/>).</returns>
    public static HelloAnchorOptions Load(string path, ILogger logger)
    {
        var result = LoadFile(path);
        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Configuration: {Warning}", warning);
        }

        return result.Options;
    }

    /// <summary>Loads configuration from <paramref name="path"/> without logging.</summary>
    /// <param name="path">Path to <c>config.json</c>.</param>
    public static ConfigLoadResult LoadFile(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return new ConfigLoadResult(HelloAnchorOptions.Default, [$"'{path}' not found; using defaults."]);
            }

            json = ReadWithRetry(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ConfigLoadResult(HelloAnchorOptions.Default, [$"Could not read '{path}' ({ex.Message}); using defaults."]);
        }

        return Parse(json);
    }

    /// <summary>
    /// Parses configuration JSON. Exposed separately from file I/O so it can be unit-tested.
    /// </summary>
    /// <param name="json">The file contents.</param>
    public static ConfigLoadResult Parse(string json)
    {
        var warnings = new List<string>();
        var defaults = HelloAnchorOptions.Default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Invalid JSON ({ex.Message}); using defaults.");
            return new ConfigLoadResult(defaults, warnings);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyIgnoreCase(document.RootElement, SectionName, out var section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"No '{SectionName}' object found; using defaults.");
                return new ConfigLoadResult(defaults, warnings);
            }

            var options = defaults;

            // Walk the properties that are present; anything absent simply keeps its default.
            foreach (var property in section.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name.ToUpperInvariant())
                {
                    case "TARGETDISPLAY":
                        options = ReadEnum<TargetDisplayMode>(value, property.Name, warnings) is { } mode
                            ? options with { TargetDisplay = mode }
                            : options;
                        break;

                    case "TARGETDEVICENAME":
                        if (value.ValueKind == JsonValueKind.Null)
                        {
                            options = options with { TargetDeviceName = null };
                        }
                        else if (value.ValueKind == JsonValueKind.String)
                        {
                            options = options with { TargetDeviceName = value.GetString()!.Trim() };
                        }
                        else
                        {
                            warnings.Add($"'{property.Name}' must be a string or null; using default.");
                        }

                        break;

                    case "TARGETPROCESSNAMES":
                        // Accept "Foo.exe" as well as "Foo" — the spec says no extension, but being lenient is harmless.
                        options = ReadStringList(value, property.Name, warnings, StripExe) is { } processes
                            ? options with { TargetProcessNames = processes }
                            : options;
                        break;

                    case "TARGETWINDOWCLASSES":
                        options = ReadStringList(value, property.Name, warnings, static s => s) is { } classes
                            ? options with { TargetWindowClasses = classes }
                            : options;
                        break;

                    case "VERIFYDELAYSMS":
                        options = ReadIntList(value, property.Name, 0, MaxVerifyDelayMs, warnings) is { } delays
                            ? options with { VerifyDelaysMs = [.. delays.Order()] }
                            : options;
                        break;

                    case "SKIPREMOTESESSIONS":
                        options = ReadBool(value, property.Name, warnings) is { } skip
                            ? options with { SkipRemoteSessions = skip }
                            : options;
                        break;

                    case "ALLOWSYSTEMTOKENFALLBACK":
                        options = ReadBool(value, property.Name, warnings) is { } fallback
                            ? options with { AllowSystemTokenFallback = fallback }
                            : options;
                        break;

                    case "AGENTRESTARTBACKOFFSECONDS":
                        options = ReadIntList(value, property.Name, 1, MaxBackoffSeconds, warnings) is { } backoff
                            ? options with { AgentRestartBackoffSeconds = backoff }
                            : options;
                        break;

                    case "LOGLEVEL":
                        options = ReadEnum<LogLevel>(value, property.Name, warnings) is { } level
                            ? options with { LogLevel = level }
                            : options;
                        break;

                    default:
                        warnings.Add($"Unknown setting '{property.Name}' ignored.");
                        break;
                }
            }

            // Cross-field rule: DeviceName mode is meaningless without a device name.
            if (options.TargetDisplay == TargetDisplayMode.DeviceName && string.IsNullOrWhiteSpace(options.TargetDeviceName))
            {
                warnings.Add("'TargetDisplay' is 'DeviceName' but 'TargetDeviceName' is empty; using 'Internal'.");
                options = options with { TargetDisplay = TargetDisplayMode.Internal };
            }

            return new ConfigLoadResult(options, warnings);
        }
    }

    /// <summary>
    /// Reads the file, tolerating brief sharing violations while an editor is saving it.
    /// </summary>
    /// <param name="path">File to read.</param>
    private static string ReadWithRetry(string path)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"file is larger than {MaxFileBytes} bytes");
                }

                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < attempts)
            {
                // Most likely a sharing violation from the editor; give it a moment.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>Case-insensitive property lookup (JSON property names are case-sensitive by default).</summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Reads an enum stored as its name. Numeric values are rejected to avoid undefined values.</summary>
    private static TEnum? ReadEnum<TEnum>(JsonElement value, string name, List<string> warnings)
        where TEnum : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<TEnum>(value.GetString(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed) &&
            !int.TryParse(value.GetString(), out _))
        {
            return parsed;
        }

        warnings.Add($"'{name}' must be one of {string.Join(", ", Enum.GetNames<TEnum>())}; using default.");
        return null;
    }

    /// <summary>Reads a JSON boolean.</summary>
    private static bool? ReadBool(JsonElement value, string name, List<string> warnings)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        warnings.Add($"'{name}' must be true or false; using default.");
        return null;
    }

    /// <summary>Reads a non-empty array of non-empty strings, applying <paramref name="normalise"/> to each.</summary>
    private static string[]? ReadStringList(JsonElement value, string name, List<string> warnings, Func<string, string> normalise)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(text))
                {
                    items.Clear();
                    break;
                }

                items.Add(normalise(text));
            }

            if (items.Count > 0)
            {
                return [.. items];
            }
        }

        warnings.Add($"'{name}' must be a non-empty array of non-empty strings; using default.");
        return null;
    }

    /// <summary>Reads a non-empty array of integers, each within [<paramref name="min"/>, <paramref name="max"/>].</summary>
    private static int[]? ReadIntList(JsonElement value, string name, int min, int max, List<string> warnings)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) || number < min || number > max)
                {
                    items.Clear();
                    break;
                }

                items.Add(number);
            }

            if (items.Count > 0)
            {
                return [.. items];
            }
        }

        warnings.Add($"'{name}' must be a non-empty array of integers between {min} and {max}; using default.");
        return null;
    }

    /// <summary>Removes a trailing <c>.exe</c> from a process name.</summary>
    private static string StripExe(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
}
