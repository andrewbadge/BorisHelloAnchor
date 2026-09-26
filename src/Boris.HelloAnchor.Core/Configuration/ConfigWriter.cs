// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Boris.HelloAnchor.Core.Configuration;

/// <summary>
/// Changes the target-display settings in <c>config.json</c> for the Settings app, leaving every other setting
/// as it was.
/// </summary>
public static class ConfigWriter
{
    /// <summary>Same leniency as <see cref="ConfigLoader"/>, so any file the loader accepts can be updated.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    /// <summary>Output formatting: matches the shipped file's two-space indent.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, IndentSize = 2 };

    /// <summary>
    /// Returns <paramref name="json"/> with <c>TargetDisplay</c> and <c>TargetMonitorId</c> set. Other settings
    /// and their order are kept; comments are not, because <see cref="JsonNode"/> can't round-trip them.
    /// </summary>
    /// <param name="json">Current file contents, or <see langword="null"/> / empty to start a new file.</param>
    /// <param name="mode">The display mode to write.</param>
    /// <param name="monitorId">
    /// Canonical monitor ID for <see cref="TargetDisplayMode.Monitor"/>; ignored (and the existing value kept)
    /// for other modes.
    /// </param>
    /// <exception cref="InvalidDataException">The existing file isn't a JSON object, or its <c>HelloAnchor</c> value isn't an object.</exception>
    public static string SetTargetDisplay(string? json, TargetDisplayMode mode, string? monitorId)
    {
        if (mode == TargetDisplayMode.Monitor && monitorId is null)
        {
            throw new ArgumentException("Monitor mode needs a monitor ID.", nameof(monitorId));
        }

        JsonObject root;
        if (string.IsNullOrWhiteSpace(json))
        {
            root = [];
        }
        else
        {
            try
            {
                root = JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject
                    ?? throw new InvalidDataException("config.json does not contain a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"config.json is not valid JSON ({ex.Message}).", ex);
            }
        }

        var section = FindKey(root, ConfigLoader.SectionName) is { } sectionKey
            ? root[sectionKey] as JsonObject ?? throw new InvalidDataException($"'{sectionKey}' in config.json is not an object.")
            : AddObject(root, ConfigLoader.SectionName);

        Set(section, nameof(HelloAnchorOptions.TargetDisplay), JsonValue.Create(mode.ToString()), after: null);
        if (mode == TargetDisplayMode.Monitor)
        {
            // Keep the three display settings together when adding the ID to an older file.
            Set(section, nameof(HelloAnchorOptions.TargetMonitorId), JsonValue.Create(monitorId),
                after: FindKey(section, nameof(HelloAnchorOptions.TargetDeviceName)) ?? FindKey(section, nameof(HelloAnchorOptions.TargetDisplay)));
        }

        return root.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="json"/> contains comments, which saving would drop.</summary>
    /// <param name="json">Current file contents.</param>
    public static bool HasComments(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is reported by SetTargetDisplay.
        }

        return false;
    }

    /// <summary>
    /// Writes <paramref name="contents"/> over <paramref name="path"/> in place (UTF-8, no BOM), so an existing
    /// file keeps its owner and ACL. A new file inherits the folder's protected ACL.
    /// </summary>
    /// <param name="path">Full path of <c>config.json</c>.</param>
    /// <param name="contents">New contents.</param>
    public static void WriteInPlace(string path, string contents)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        if (bytes.Length > ConfigLoader.MaxFileBytes)
        {
            throw new InvalidDataException($"The new configuration is larger than {ConfigLoader.MaxFileBytes} bytes.");
        }

        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.SetLength(0);
        stream.Write(bytes);
    }

    /// <summary>Sets <paramref name="name"/>, reusing an existing key's spelling or inserting a new key after <paramref name="after"/>.</summary>
    private static void Set(JsonObject obj, string name, JsonNode? value, string? after)
    {
        if (FindKey(obj, name) is { } existing)
        {
            obj[existing] = value;
            return;
        }

        var index = after is null ? 0 : obj.IndexOf(after) + 1;
        obj.Insert(index, name, value);
    }

    /// <summary>Adds an empty object property and returns it.</summary>
    private static JsonObject AddObject(JsonObject parent, string name)
    {
        var child = new JsonObject();
        parent.Add(name, child);
        return child;
    }

    /// <summary>Case-insensitive key lookup, matching how <see cref="ConfigLoader"/> reads the file.</summary>
    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
