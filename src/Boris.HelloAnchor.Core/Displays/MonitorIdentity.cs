// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Boris.HelloAnchor.Core.Displays;

/// <summary>What a monitor's EDID says about it (only the fields HelloAnchor uses).</summary>
/// <param name="ManufacturerId">Three-letter PNP manufacturer ID, e.g. <c>DEL</c>.</param>
/// <param name="ProductCode">Manufacturer's product code.</param>
/// <param name="SerialNumber">Numeric serial from the base block (0 when the maker didn't set one).</param>
/// <param name="SerialText">Serial number descriptor (tag <c>0xFF</c>), if present.</param>
/// <param name="Name">Monitor name descriptor (tag <c>0xFC</c>), if present.</param>
public sealed record EdidInfo(string ManufacturerId, ushort ProductCode, uint SerialNumber, string? SerialText, string? Name)
{
    /// <summary>Model ID in the same form Windows uses for the device's hardware ID, e.g. <c>DEL41B8</c>.</summary>
    public string ModelId => MonitorIdentity.FormatModel(ManufacturerId, ProductCode);

    /// <summary>
    /// The stable monitor ID: the model plus the serial when the monitor reports one, e.g.
    /// <c>DEL41B8-5KC0Q83</c>. The serial descriptor is preferred because it is what's printed on the label.
    /// </summary>
    public string MonitorId
    {
        get
        {
            var text = MonitorIdentity.CleanSerial(SerialText);
            var serial = text is not null ? text
                : SerialNumber != 0 ? SerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return serial is null ? ModelId : $"{ModelId}{MonitorIdentity.Separator}{serial}";
        }
    }
}

/// <summary>
/// Stable monitor IDs (SPEC §7.4). GDI names such as <c>\\.\DISPLAY2</c> are handed out in the order Windows
/// meets the displays, so they change on docking, re-plugging or driver updates. A monitor ID is built from
/// the monitor's own EDID instead, so it follows the physical screen whichever port or dock it is on.
/// </summary>
/// <remarks>
/// <para>
/// Form: <c>MMMPPPP</c> or <c>MMMPPPP-SERIAL</c>, where <c>MMM</c> is the PNP manufacturer ID, <c>PPPP</c> the
/// product code in hex (together the model, matching the Windows hardware ID <c>DISPLAY\MMMPPPP</c>), and
/// <c>SERIAL</c> the monitor's serial number.
/// </para>
/// <para>
/// A configured ID without a serial matches any monitor of that model. A monitor whose serial can't be read
/// matches a configured ID for its model. Pure logic, no Win32, so it can be unit-tested.
/// </para>
/// </remarks>
public static class MonitorIdentity
{
    /// <summary>Separates the model from the serial.</summary>
    public const char Separator = '-';

    /// <summary>Length of the model part: three letters plus four hex digits.</summary>
    private const int ModelLength = 7;

    /// <summary>Longest serial accepted in configuration. EDID text descriptors hold at most 13 characters.</summary>
    private const int MaxSerialLength = 32;

    /// <summary>EDID base block length.</summary>
    private const int EdidBlockLength = 128;

    /// <summary>Formats a model ID, e.g. <c>DEL</c> + <c>0x41B8</c> → <c>DEL41B8</c>.</summary>
    /// <param name="manufacturerId">Three-letter PNP ID.</param>
    /// <param name="productCode">Product code.</param>
    public static string FormatModel(string manufacturerId, ushort productCode) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{manufacturerId.ToUpperInvariant()}{productCode:X4}");

    /// <summary>
    /// Parses the base block of an EDID. Returns <see langword="null"/> for anything that isn't a valid EDID
    /// (too short, wrong header, bad manufacturer letters).
    /// </summary>
    /// <param name="edid">Raw EDID bytes, e.g. from the monitor's <c>Device Parameters\EDID</c> registry value.</param>
    public static EdidInfo? ParseEdid(ReadOnlySpan<byte> edid)
    {
        ReadOnlySpan<byte> header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        if (edid.Length < EdidBlockLength || !edid[..8].SequenceEqual(header))
        {
            return null;
        }

        // Bytes 8-9: big-endian, three 5-bit letters where 1 = 'A'.
        var packed = (edid[8] << 8) | edid[9];
        Span<char> letters = stackalloc char[3];
        for (var i = 0; i < 3; i++)
        {
            var code = (packed >> (10 - (5 * i))) & 0x1F;
            if (code is < 1 or > 26)
            {
                return null;
            }

            letters[i] = (char)('A' + code - 1);
        }

        var product = (ushort)(edid[10] | (edid[11] << 8));
        var serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        // Four 18-byte descriptors at 54, 72, 90, 108. Display descriptors start 00 00 00 <tag>.
        string? serialText = null;
        string? name = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            var descriptor = edid.Slice(offset, 18);
            if (descriptor[0] != 0 || descriptor[1] != 0 || descriptor[2] != 0)
            {
                continue;
            }

            switch (descriptor[3])
            {
                case 0xFF:
                    serialText ??= ReadDescriptorText(descriptor[5..]);
                    break;
                case 0xFC:
                    name ??= ReadDescriptorText(descriptor[5..]);
                    break;
            }
        }

        return new EdidInfo(new string(letters), product, serial, serialText, name);
    }

    /// <summary>
    /// Extracts the model ID from a monitor device interface path, e.g.
    /// <c>\\?\DISPLAY#DEL41B8#5&amp;2a8c1b0&amp;0&amp;UID4352#{e6f07b5f-...}</c> → <c>DEL41B8</c>.
    /// Used when the EDID can't be read.
    /// </summary>
    /// <param name="devicePath">The <c>monitorDevicePath</c> reported by <c>DisplayConfigGetDeviceInfo</c>.</param>
    public static string? ModelFromDevicePath(string? devicePath)
    {
        var parts = SplitDevicePath(devicePath);
        return parts is not null && IsModel(parts.Value.HardwareId) ? parts.Value.HardwareId.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Converts a monitor device interface path to its device instance ID, e.g. <c>DISPLAY\DEL41B8\5&amp;2a8c1b0&amp;0&amp;UID4352</c>,
    /// which is the key under <c>HKLM\SYSTEM\CurrentControlSet\Enum</c> that holds the EDID.
    /// </summary>
    /// <param name="devicePath">The <c>monitorDevicePath</c> reported by <c>DisplayConfigGetDeviceInfo</c>.</param>
    public static string? InstanceIdFromDevicePath(string? devicePath)
    {
        var parts = SplitDevicePath(devicePath);
        if (parts is null)
        {
            return null;
        }

        // The registry path is built from these parts, so reject anything that could step outside the key.
        var (enumerator, hardwareId, instance) = parts.Value;
        foreach (var part in (ReadOnlySpan<string>)[enumerator, hardwareId, instance])
        {
            if (part.Length == 0 || part.Contains('\\') || part.Contains('/') || part.Contains(".."))
            {
                return null;
            }
        }

        return $@"{enumerator}\{hardwareId}\{instance}";
    }

    /// <summary>
    /// Validates a monitor ID from configuration and returns it in canonical form (model upper-cased, serial
    /// kept as typed but trimmed).
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="monitorId">The canonical ID.</param>
    public static bool TryNormalise(string? value, [NotNullWhen(true)] out string? monitorId)
    {
        monitorId = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < ModelLength || !IsModel(text[..ModelLength]))
        {
            return false;
        }

        var model = text[..ModelLength].ToUpperInvariant();
        if (text.Length == ModelLength)
        {
            monitorId = model;
            return true;
        }

        var serial = text[(ModelLength + 1)..];
        if (text[ModelLength] != Separator || serial.Length == 0 || serial.Length > MaxSerialLength || !serial.All(IsSerialChar))
        {
            return false;
        }

        monitorId = model + Separator + serial;
        return true;
    }

    /// <summary>How well an attached monitor matches a configured ID.</summary>
    /// <param name="monitorId">The attached monitor's ID (may be <see langword="null"/> if unknown).</param>
    /// <param name="configuredId">The canonical configured ID.</param>
    public static MonitorMatch Match(string? monitorId, string configuredId)
    {
        if (string.IsNullOrEmpty(monitorId) || monitorId.Length < ModelLength)
        {
            return MonitorMatch.None;
        }

        // Serials are compared case-insensitively too: some tools print them upper-case.
        if (string.Equals(monitorId, configuredId, StringComparison.OrdinalIgnoreCase))
        {
            return MonitorMatch.Exact;
        }

        var sameModel = string.Equals(monitorId[..ModelLength], configuredId[..ModelLength], StringComparison.OrdinalIgnoreCase);
        var eitherHasNoSerial = monitorId.Length == ModelLength || configuredId.Length == ModelLength;
        return sameModel && eitherHasNoSerial ? MonitorMatch.Model : MonitorMatch.None;
    }

    /// <summary>Reads the 13-byte text field of an EDID display descriptor (ends at 0x0A, padded with spaces).</summary>
    private static string? ReadDescriptorText(ReadOnlySpan<byte> field)
    {
        var builder = new StringBuilder(field.Length);
        foreach (var b in field)
        {
            if (b == 0x0A || b == 0x00)
            {
                break;
            }

            // Printable ASCII only; anything else is garbage from a sloppy EDID.
            if (b is >= 0x20 and < 0x7F)
            {
                builder.Append((char)b);
            }
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Splits <c>\\?\DISPLAY#HWID#INSTANCE#{GUID}</c> into its three device-instance parts.</summary>
    private static (string Enumerator, string HardwareId, string Instance)? SplitDevicePath(string? devicePath)
    {
        const string prefix = @"\\?\";
        if (devicePath is null || !devicePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = devicePath[prefix.Length..].Split('#');
        return parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : null;
    }

    /// <summary>Three ASCII letters followed by four hex digits.</summary>
    private static bool IsModel(string text) =>
        text.Length == ModelLength &&
        text[..3].All(char.IsAsciiLetter) &&
        text[3..].All(char.IsAsciiHexDigit);

    /// <summary>Drops characters a configured serial may not contain (spaces, quotes, backslashes).</summary>
    /// <param name="serial">Serial text from the EDID.</param>
    internal static string? CleanSerial(string? serial)
    {
        var cleaned = serial is null ? null : new string([.. serial.Where(IsSerialChar)]);
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    /// <summary>Characters allowed in a configured serial: printable ASCII except spaces and quotes.</summary>
    private static bool IsSerialChar(char c) => c is > ' ' and < (char)0x7F and not '"' and not '\\';
}

/// <summary>Result of <see cref="MonitorIdentity.Match"/>.</summary>
public enum MonitorMatch
{
    /// <summary>A different monitor.</summary>
    None,

    /// <summary>Same model, and one side has no serial to compare.</summary>
    Model,

    /// <summary>Same model and serial (or both without a serial).</summary>
    Exact,
}
