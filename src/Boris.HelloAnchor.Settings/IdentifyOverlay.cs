// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core.Displays;

namespace Boris.HelloAnchor.Settings;

/// <summary>
/// A borderless label shown in the middle of one monitor by "Identify monitors", naming the monitor and its ID.
/// Click it to close it early.
/// </summary>
internal sealed class IdentifyOverlay : Form
{
    private readonly DisplayMonitor _monitor;

    /// <summary>Creates the overlay for <paramref name="monitor"/>.</summary>
    public IdentifyOverlay(DisplayMonitor monitor)
    {
        _monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Opacity = 0.92;

        // Start on the target monitor so any DPI change happens before the final placement in OnShown.
        Location = new Point(monitor.Bounds.Left, monitor.Bounds.Top);

        // Built-in panels often have no name of their own, so their name is already "Built-in display".
        var kind = monitor.IsInternal ? "Built-in" : "External";
        if (monitor.IsPrimary)
        {
            kind += ", main display";
        }

        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 18F, FontStyle.Regular),
            Text = $"{monitor.FriendlyName}{Environment.NewLine}{kind}{Environment.NewLine}{monitor.MonitorId ?? "(no ID)"}",
        };
        label.Click += (_, _) => Close();
        Controls.Add(label);
    }

    /// <summary>Don't take focus from the settings window.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Centre a box a third of the monitor wide, now that the window is on that monitor's DPI.
        var bounds = _monitor.Bounds;
        var width = Math.Max(bounds.Width / 3, 360);
        var height = Math.Max(bounds.Height / 4, 200);
        SetBounds(bounds.Left + ((bounds.Width - width) / 2), bounds.Top + ((bounds.Height - height) / 2), width, height);
    }
}
