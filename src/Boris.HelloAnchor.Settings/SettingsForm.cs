// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Globalization;
using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Core.Displays;
using Boris.HelloAnchor.Displays;

namespace Boris.HelloAnchor.Settings;

/// <summary>
/// The settings window (SPEC §7.7): choose where the prompt goes and save it to <c>config.json</c>. The agent
/// picks the change up through its config watcher, so nothing needs restarting.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly RadioButton _internal = NewRadio("&Built-in laptop display (default)");
    private readonly RadioButton _primary = NewRadio("&Main display (whichever monitor Windows treats as the main display)");
    private readonly RadioButton _monitor = NewRadio("A &specific monitor:");
    private readonly RadioButton _deviceName = NewRadio(string.Empty);
    private readonly ListView _monitors = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill,
        Margin = new Padding(24, 3, 3, 3),
    };

    private readonly Button _identify = NewButton("&Identify monitors");
    private readonly Button _refresh = NewButton("&Refresh");
    private readonly Button _save = NewButton("Sa&ve");
    private readonly Button _close = NewButton("Close");
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 9, 3, 3), MaximumSize = new Size(640, 0) };

    /// <summary>Current contents of the configuration, reloaded on Refresh and after saving.</summary>
    private HelloAnchorOptions _options = HelloAnchorOptions.Default;

    /// <summary>Open identify overlays, closed together.</summary>
    private readonly List<Form> _overlays = [];

    /// <summary>Creates the window and loads the current configuration.</summary>
    public SettingsForm()
    {
        Text = "Boris HelloAnchor Settings";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 460);
        Size = new Size(760, 520);
        Padding = new Padding(12);
        AcceptButton = _save;
        CancelButton = _close;

        // Column widths are pixels, so scale them for the display's DPI.
        _monitors.Columns.Add("Monitor", LogicalToDeviceUnits(200));
        _monitors.Columns.Add("Connection", LogicalToDeviceUnits(170));
        _monitors.Columns.Add("Resolution", LogicalToDeviceUnits(100));
        _monitors.Columns.Add("Monitor ID", LogicalToDeviceUnits(180));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = false };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "Where should the Windows Hello prompt appear?",
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size * 1.25F, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 9),
        };

        var listButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(21, 0, 0, 0), WrapContents = false };
        listButtons.Controls.AddRange([_identify, _refresh]);

        var note = new Label
        {
            Text = "If the chosen display isn't connected (for example, when undocked), the prompt goes to the built-in display instead.",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(3, 9, 3, 3),
        };

        var dialogButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 9, 0, 0),
        };
        dialogButtons.Controls.AddRange([_close, _save]);

        layout.Controls.Add(heading);
        layout.Controls.Add(_internal);
        layout.Controls.Add(_primary);
        layout.Controls.Add(_monitor);
        layout.Controls.Add(_monitors);
        layout.Controls.Add(listButtons);
        layout.Controls.Add(_deviceName);
        layout.Controls.Add(note);
        layout.Controls.Add(_status);
        layout.Controls.Add(dialogButtons);
        Controls.Add(layout);

        _monitor.CheckedChanged += (_, _) => UpdateEnabled();
        _monitors.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _monitors.ItemActivate += (_, _) => _monitor.Checked = true;
        _monitors.MouseDown += (_, _) => _monitor.Checked = true;
        _identify.Click += (_, _) => Identify();
        _refresh.Click += (_, _) => LoadState(keepSelection: true);
        _save.Click += (_, _) => Save();
        _close.Click += (_, _) => Close();
        FormClosed += (_, _) => CloseOverlays();

        LoadState(keepSelection: false);
    }

    /// <summary>Reads the configuration and the attached monitors, and shows them.</summary>
    /// <param name="keepSelection">Keep the radio and list choices on screen rather than showing the saved ones.</param>
    private void LoadState(bool keepSelection)
    {
        var selectedMode = keepSelection ? SelectedMode() : null;
        var selectedId = keepSelection ? SelectedMonitorId() : null;

        var result = ConfigLoader.LoadFile(HelloAnchorPaths.ConfigFile);
        _options = result.Options;

        var mode = selectedMode ?? _options.TargetDisplay;
        var monitorId = selectedId ?? _options.TargetMonitorId;

        // The legacy GDI-name mode is only offered when it is what the file already uses.
        _deviceName.Visible = _options.TargetDisplay == TargetDisplayMode.DeviceName;
        _deviceName.Text = $"&GDI device name set in config.json: {_options.TargetDeviceName} (can change when docking; prefer a specific monitor)";

        FillMonitors(monitorId);

        (mode switch
        {
            TargetDisplayMode.Primary => _primary,
            TargetDisplayMode.Monitor => _monitor,
            TargetDisplayMode.DeviceName when _deviceName.Visible => _deviceName,
            _ => _internal,
        }).Checked = true;

        _status.ForeColor = SystemColors.ControlText;
        _status.Text = result.Warnings.Count == 0
            ? $"Settings file: {HelloAnchorPaths.ConfigFile}"
            : "The settings file has problems; the defaults are used for these settings:" + Environment.NewLine +
              string.Join(Environment.NewLine, result.Warnings.Select(w => "• " + w));
        UpdateEnabled();
    }

    /// <summary>Lists the attached monitors, plus the configured one if it isn't connected, and selects <paramref name="monitorId"/>.</summary>
    private void FillMonitors(string? monitorId)
    {
        var monitors = DisplayTopology.Enumerate();
        _monitors.BeginUpdate();
        _monitors.Items.Clear();

        foreach (var monitor in monitors)
        {
            var connection = monitor.IsInternal ? "Built-in" : "External";
            if (monitor.IsPrimary)
            {
                connection += ", main display";
            }

            var item = new ListViewItem(
            [
                monitor.FriendlyName,
                connection,
                string.Create(CultureInfo.CurrentCulture, $"{monitor.Bounds.Width} × {monitor.Bounds.Height}"),
                monitor.MonitorId ?? "(unavailable)",
            ])
            {
                Tag = monitor,
            };

            if (monitor.MonitorId is null)
            {
                item.ForeColor = SystemColors.GrayText;
            }

            _monitors.Items.Add(item);
        }

        ListViewItem? selected = null;
        if (monitorId is not null)
        {
            var match = TargetDisplaySelector.FindMonitor(monitors, monitorId);
            selected = _monitors.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, match));
            if (selected is null)
            {
                // Keep a configured monitor that is unplugged right now, so saving doesn't lose it.
                selected = new ListViewItem(["(not connected)", string.Empty, string.Empty, monitorId])
                {
                    Tag = monitorId,
                    ForeColor = SystemColors.GrayText,
                };
                _monitors.Items.Add(selected);
            }
        }

        if (selected is not null)
        {
            selected.Selected = true;
            selected.EnsureVisible();
        }

        _monitors.EndUpdate();
    }

    /// <summary>Saves the chosen display to <c>config.json</c>.</summary>
    private void Save()
    {
        var mode = SelectedMode() ?? TargetDisplayMode.Internal;
        var monitorId = mode == TargetDisplayMode.Monitor ? SelectedMonitorId() : null;
        if (mode == TargetDisplayMode.Monitor && monitorId is null)
        {
            ShowError(_monitors.SelectedItems.Count == 0
                ? "Choose a monitor from the list first."
                : "Windows didn't report an ID for that monitor, so HelloAnchor can't recognise it reliably. Choose another option.");
            return;
        }

        var path = HelloAnchorPaths.ConfigFile;
        if (!Directory.Exists(HelloAnchorPaths.DataDirectory))
        {
            // Creating it here would inherit %ProgramData%'s permissive ACL; the installer secures it.
            ShowError($"'{HelloAnchorPaths.DataDirectory}' doesn't exist. Is Boris HelloAnchor installed?");
            return;
        }

        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (ConfigWriter.HasComments(existing) &&
                MessageBox.Show(this, "config.json contains comments, which will be removed when it is saved. Continue?",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }

            var updated = ConfigWriter.SetTargetDisplay(existing, mode, monitorId);

            // Belt and braces: never write a file that wouldn't load as intended.
            var check = ConfigLoader.Parse(updated).Options;
            if (check.TargetDisplay != mode || (mode == TargetDisplayMode.Monitor && check.TargetMonitorId != monitorId))
            {
                ShowError("The new settings didn't validate, so nothing was saved.");
                return;
            }

            ConfigWriter.WriteInPlace(path, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"Couldn't save the settings: {ex.Message}");
            return;
        }

        LoadState(keepSelection: false);
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Saved. HelloAnchor applies the change within about a second; no restart is needed.";
    }

    /// <summary>Shows a label on every monitor for a few seconds, so people can match the list to the screens.</summary>
    private void Identify()
    {
        CloseOverlays();
        foreach (var monitor in DisplayTopology.Enumerate())
        {
            var overlay = new IdentifyOverlay(monitor);
            overlay.FormClosed += (_, _) => _overlays.Remove(overlay);
            _overlays.Add(overlay);
            overlay.Show(this);
        }

        var timer = new System.Windows.Forms.Timer { Interval = 4000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            CloseOverlays();
        };
        timer.Start();
    }

    /// <summary>Closes any open identify overlays.</summary>
    private void CloseOverlays()
    {
        foreach (var overlay in _overlays.ToArray())
        {
            overlay.Close();
        }
    }

    /// <summary>The mode for the checked radio button, or <see langword="null"/> if none is checked.</summary>
    private TargetDisplayMode? SelectedMode() =>
        _internal.Checked ? TargetDisplayMode.Internal
        : _primary.Checked ? TargetDisplayMode.Primary
        : _monitor.Checked ? TargetDisplayMode.Monitor
        : _deviceName.Checked ? TargetDisplayMode.DeviceName
        : null;

    /// <summary>The monitor ID of the selected list row, or <see langword="null"/>.</summary>
    private string? SelectedMonitorId() => _monitors.SelectedItems.Count == 0 ? null : _monitors.SelectedItems[0].Tag switch
    {
        DisplayMonitor monitor => monitor.MonitorId,
        string configured => configured,
        _ => null,
    };

    /// <summary>Enables Save only when the choice is complete.</summary>
    private void UpdateEnabled() =>
        _save.Enabled = !_monitor.Checked || SelectedMonitorId() is not null;

    /// <summary>Shows an error in the status line.</summary>
    private void ShowError(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = message;
    }

    /// <summary>Creates an auto-sized radio button.</summary>
    private static RadioButton NewRadio(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };

    /// <summary>Creates an auto-sized push button.</summary>
    private static Button NewButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 0) };
}
