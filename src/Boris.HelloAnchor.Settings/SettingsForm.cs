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
    private readonly PixelRadio _internal = new("&Built-in laptop display");
    private readonly PixelRadio _primary = new("&Main display");
    private readonly PixelRadio _monitor = new("A &specific monitor");
    private readonly PixelRadio _deviceName = new(string.Empty);
    private readonly Control _deviceNameRow;
    private readonly ListView _monitors = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        OwnerDraw = true,
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Background,
        ForeColor = Theme.Text,
        Dock = DockStyle.Fill,
    };

    private readonly Button _identify = Theme.Button("&Identify monitors", primary: false);
    private readonly Button _refresh = Theme.Button("&Refresh", primary: false);
    private readonly Button _save = Theme.Button("Sa&ve", primary: true);
    private readonly Button _close = Theme.Button("Close", primary: false);
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 12, 0) };

    private readonly StatusCard _targetCard = new("PROMPT GOES TO");
    private readonly StatusCard _monitorsCard = new("MONITORS");
    private readonly StatusCard _configCard = new("CONFIG");

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
        MinimumSize = new Size(720, 720);
        Size = new Size(840, 780);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Font(9.75F);
        AcceptButton = _save;
        CancelButton = _close;

        // Widths are set as proportions of the list in FitColumns, so they follow resizing and DPI changes.
        _monitors.Columns.Add("Monitor");
        _monitors.Columns.Add("Connection");
        _monitors.Columns.Add("Resolution");
        _monitors.Columns.Add("Monitor ID");
        _monitors.SmallImageList = new ImageList { ImageSize = new Size(1, LogicalToDeviceUnits(28)) }; // Sets the row height.
        _monitors.DrawColumnHeader += DrawMonitorHeader;
        _monitors.DrawSubItem += DrawMonitorCell;
        _monitors.Resize += (_, _) => FitColumns();

        // Header: product name and summary cards.
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(20, 14, 20, 16) };
        for (var i = 0; i < 3; i++)
        {
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        }

        var title = Theme.Label($"BORIS HELLOANCHOR   V{typeof(SettingsForm).Assembly.GetName().Version?.ToString(3)}", 8.25F, color: Theme.Muted);
        title.Margin = new Padding(4, 0, 0, 10);
        header.Controls.Add(title, 0, 0);
        header.SetColumnSpan(title, 3);
        header.Controls.Add(_targetCard, 0, 1);
        header.Controls.Add(_monitorsCard, 1, 1);
        header.Controls.Add(_configCard, 2, 1);
        header.Paint += (_, e) => DrawLine(e.Graphics, header.Height - 1, header.Width);

        // Body: the display choice.
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(24, 18, 24, 12) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++)
        {
            body.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, 100));
        }

        var heading = Theme.Label("PROMPT DISPLAY", 15F, FontStyle.Bold);
        heading.Margin = new Padding(0, 0, 0, 10);

        var options = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 1), Margin = new Padding(0, 0, 0, 18) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.Controls.Add(OptionRow(_internal, "Default. The laptop's own screen."));
        options.Controls.Add(OptionRow(_primary, "Whichever monitor Windows treats as the main display."));
        options.Controls.Add(OptionRow(_monitor, "The monitor chosen in the list below."));
        options.Controls.Add(_deviceNameRow = OptionRow(_deviceName, "Set in config.json. Can change when docking; prefer a specific monitor."));
        options.Paint += (_, e) => Theme.PaintRows(options, e.Graphics);
        options.Resize += (_, _) => options.Invalidate();

        // A panel in the border colour with 1 px padding draws the list's frame.
        var listFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Border, Margin = new Padding(0, 0, 0, 10) };
        listFrame.Controls.Add(_monitors);

        var listButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _identify.Margin = new Padding(0, 0, 8, 0);
        listButtons.Controls.AddRange([_identify, _refresh]);

        var note = Theme.Label(
            "If the chosen display isn't connected (for example, when undocked), the prompt goes to the built-in display instead.",
            8.25F,
            color: Theme.Muted);
        note.Dock = DockStyle.Fill;
        note.Margin = new Padding(0, 12, 0, 0);

        body.Controls.Add(heading);
        body.Controls.Add(Section("WHERE SHOULD THE WINDOWS HELLO PROMPT APPEAR?"));
        body.Controls.Add(options);
        body.Controls.Add(Section("CONNECTED MONITORS"));
        body.Controls.Add(listFrame);
        body.Controls.Add(listButtons);
        body.Controls.Add(note);

        // Footer: status line and dialog buttons.
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(24, 14, 24, 14) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var dialogButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        dialogButtons.Controls.AddRange([_save, _close]);
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(dialogButtons, 1, 0);
        footer.Paint += (_, e) => DrawLine(e.Graphics, 0, footer.Width);

        // Fill first: docking runs in reverse order of addition.
        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(footer);

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

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    /// <summary>Reads the configuration and the attached monitors, and shows them.</summary>
    /// <param name="keepSelection">Keep the radio and list choices on screen rather than showing the saved ones.</param>
    private void LoadState(bool keepSelection)
    {
        var selectedMode = keepSelection ? SelectedMode() : null;
        var selectedId = keepSelection ? SelectedMonitorId() : null;

        var result = ConfigLoader.LoadFile(HelloAnchorPaths.ConfigFile);
        _options = result.Options;

        // Show the choice saved for this monitor setup, if there is one; otherwise the top-level setting.
        var monitors = DisplayTopology.Enumerate();
        var profile = TargetDisplaySelector.FindProfile(monitors, _options);
        var mode = selectedMode ?? profile?.TargetDisplay ?? _options.TargetDisplay;
        var monitorId = selectedId ?? (profile is null ? _options.TargetMonitorId : profile.TargetMonitorId);

        // The legacy GDI-name mode is only offered when it is what the file already uses.
        _deviceNameRow.Visible = profile is null && _options.TargetDisplay == TargetDisplayMode.DeviceName;
        _deviceName.Text = $"&GDI device name: {_options.TargetDeviceName}";

        FillMonitors(monitors, monitorId);
        UpdateCards(monitors, result.Warnings.Count);

        (mode switch
        {
            TargetDisplayMode.Primary => _primary,
            TargetDisplayMode.Monitor => _monitor,
            TargetDisplayMode.DeviceName when _deviceNameRow.Visible => _deviceName,
            _ => _internal,
        }).Checked = true;

        _status.ForeColor = result.Warnings.Count == 0 ? Theme.Muted : Theme.Warning;
        _status.Text = result.Warnings.Count == 0
            ? string.Empty
            : "The settings file has problems; the defaults are used for these settings:" + Environment.NewLine +
              string.Join(Environment.NewLine, result.Warnings.Select(w => "• " + w));
        UpdateEnabled();
    }

    /// <summary>Shows where the saved settings send the prompt right now, the attached monitors, and the config file's state.</summary>
    private void UpdateCards(IReadOnlyList<DisplayMonitor> monitors, int warningCount)
    {
        var selection = TargetDisplaySelector.Select(monitors, _options);
        var mode = (selection?.Profile?.TargetDisplay ?? _options.TargetDisplay) switch
        {
            TargetDisplayMode.Primary => "MAIN",
            TargetDisplayMode.Monitor => "MONITOR",
            TargetDisplayMode.DeviceName => "DEVICE",
            _ => "BUILT-IN",
        };
        if (selection is null)
        {
            _targetCard.Set(mode, Theme.Error, "No display found");
        }
        else if (selection.IsFallback)
        {
            _targetCard.Set(mode, Theme.Warning, $"Not connected · using {selection.Monitor.FriendlyName}");
        }
        else
        {
            _targetCard.Set(mode, Theme.Accent, selection.Monitor.FriendlyName);
        }

        var builtIn = monitors.Count(m => m.IsInternal);
        _monitorsCard.Set($"{monitors.Count} CONNECTED", Theme.Text, $"{builtIn} built-in · {monitors.Count - builtIn} external");

        _configCard.Set(
            warningCount == 0 ? "OK" : $"{warningCount} PROBLEM{(warningCount == 1 ? string.Empty : "S")}",
            warningCount == 0 ? Theme.Ok : Theme.Warning,
            HelloAnchorPaths.ConfigFile);
    }

    /// <summary>Lists the attached monitors, plus the configured one if it isn't connected, and selects <paramref name="monitorId"/>.</summary>
    private void FillMonitors(IReadOnlyList<DisplayMonitor> monitors, string? monitorId)
    {
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
                item.ForeColor = Theme.Muted;
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
                    ForeColor = Theme.Muted,
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
        FitColumns();
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

            // The choice is remembered for the monitors attached right now. The top-level setting is only
            // written when there's no setup to key it to (no monitor reported an ID) or for the legacy mode.
            var monitors = DisplayTopology.Enumerate();
            var setup = TargetDisplaySelector.SetupKey(monitors);
            var useProfile = setup.Count > 0 && mode != TargetDisplayMode.DeviceName;
            var updated = useProfile
                ? ConfigWriter.SetProfile(existing, setup, mode, monitorId)
                : ConfigWriter.SetTargetDisplay(existing, mode, monitorId);

            // Belt and braces: never write a file that wouldn't load as intended.
            var check = ConfigLoader.Parse(updated).Options;
            var (savedMode, savedId) = useProfile && TargetDisplaySelector.FindProfile(monitors, check) is { } profile
                ? (profile.TargetDisplay, profile.TargetMonitorId)
                : (check.TargetDisplay, check.TargetMonitorId);
            if (savedMode != mode || (mode == TargetDisplayMode.Monitor && savedId != monitorId))
            {
                ShowError("The new settings didn't validate, so nothing was saved.");
                return;
            }

            ConfigWriter.WriteInPlace(path, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"Couldn't save the settings: {ex.Message}", ex);
            return;
        }

        LoadState(keepSelection: false);
        _status.ForeColor = Theme.Ok;
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
    private void ShowError(string message, Exception? exception = null)
    {
        _status.ForeColor = Theme.Error;
        Program.Log.Warning(exception, "{Message}", message);
        _status.Text = message;
    }

    /// <summary>A row of the options box: the radio button with a muted description under it. Clicking anywhere on the row selects it.</summary>
    private static Control OptionRow(PixelRadio radio, string description)
    {
        var note = Theme.Label(description, 8.25F, color: Theme.Muted);
        note.Margin = new Padding(PixelRadio.TextIndent, 2, 0, 0);

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 10),
            Margin = new Padding(1, 1, 1, 0),
            Cursor = Cursors.Hand,
        };
        row.Controls.Add(radio);
        row.Controls.Add(note);
        row.Click += (_, _) => radio.Checked = true;
        note.Click += (_, _) => radio.Checked = true;
        return row;
    }

    /// <summary>A small bold section caption.</summary>
    private static Label Section(string text)
    {
        var label = Theme.Label(text, 8.25F, FontStyle.Bold);
        label.Margin = new Padding(0, 0, 0, 8);
        return label;
    }

    /// <summary>Draws a full-width divider at <paramref name="y"/>.</summary>
    private static void DrawLine(Graphics graphics, int y, int width)
    {
        using var pen = new Pen(Theme.Border);
        graphics.DrawLine(pen, 0, y, width, y);
    }

    /// <summary>
    /// Sizes the columns as shares of the list's width; the last takes the rest, so there's no unpainted header
    /// area to its right.
    /// </summary>
    private void FitColumns()
    {
        if (_monitors.Columns.Count != ColumnShares.Length + 1)
        {
            return;
        }

        var width = _monitors.ClientSize.Width;
        var used = 0;
        for (var i = 0; i < ColumnShares.Length; i++)
        {
            _monitors.Columns[i].Width = (int)(width * ColumnShares[i]);
            used += _monitors.Columns[i].Width;
        }

        _monitors.Columns[^1].Width = Math.Max(0, width - used);
    }

    /// <summary>Width shares of every column but the last.</summary>
    private static readonly float[] ColumnShares = [0.28F, 0.30F, 0.17F];

    /// <summary>Draws a column header in the dark theme.</summary>
    private void DrawMonitorHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var background = new SolidBrush(Theme.Surface))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        DrawLine(e.Graphics, e.Bounds.Bottom - 1, e.Bounds.Right);
        using var font = new Font(_monitors.Font.FontFamily, _monitors.Font.Size * 0.85F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text.ToUpperInvariant(), font, CellText(e.Bounds), Theme.Muted, CellFlags);
    }

    /// <summary>Draws a list cell, with the selected row in the accent colour.</summary>
    private void DrawMonitorCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        var selected = e.Item?.Selected == true;
        using (var background = new SolidBrush(selected ? Theme.AccentDim : Theme.Background))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        if (selected && e.ColumnIndex == 0)
        {
            using var bar = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top, LogicalToDeviceUnits(3), e.Bounds.Height);
        }

        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, _monitors.Font, CellText(e.Bounds), selected ? Theme.Text : e.Item?.ForeColor ?? Theme.Text, CellFlags);
    }

    private const TextFormatFlags CellFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    /// <summary>Cell bounds less the text padding.</summary>
    private Rectangle CellText(Rectangle bounds)
    {
        var pad = LogicalToDeviceUnits(10);
        return new Rectangle(bounds.X + pad, bounds.Y, Math.Max(0, bounds.Width - pad), bounds.Height);
    }
}
