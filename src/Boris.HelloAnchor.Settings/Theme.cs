// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Runtime.InteropServices;

namespace Boris.HelloAnchor.Settings;

/// <summary>Colours, fonts and small control factories for the dark, monospace look of the Settings app.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(0x16, 0x15, 0x12);
    public static readonly Color Surface = Color.FromArgb(0x1F, 0x1E, 0x1A);
    public static readonly Color Border = Color.FromArgb(0x3A, 0x38, 0x32);
    public static readonly Color Text = Color.FromArgb(0xE8, 0xE4, 0xDA);
    public static readonly Color Muted = Color.FromArgb(0x8F, 0x89, 0x7C);
    public static readonly Color Accent = Color.FromArgb(0x6C, 0xC4, 0xEE);
    public static readonly Color AccentHover = Color.FromArgb(0x8F, 0xD4, 0xF4);
    public static readonly Color AccentDim = Color.FromArgb(0x1E, 0x36, 0x44);
    public static readonly Color Warning = Color.FromArgb(0xF5, 0xB9, 0x42);
    public static readonly Color Ok = Color.FromArgb(0x6B, 0xC2, 0x6B);
    public static readonly Color Error = Color.FromArgb(0xE8, 0x6A, 0x5C);

    /// <summary>Consolas ships with every supported Windows version.</summary>
    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Consolas", size, style);

    /// <summary>Creates an auto-sized label.</summary>
    public static Label Label(string text, float size = 9.75F, FontStyle style = FontStyle.Regular, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Font(size, style),
        ForeColor = color ?? Text,
        Margin = Padding.Empty,
    };

    /// <summary>Creates a flat button: filled with the accent colour when <paramref name="primary"/>, outlined otherwise.</summary>
    public static Button Button(string text, bool primary)
    {
        var button = new Button
        {
            Text = text.ToUpperInvariant(),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            AutoSize = true,
            MinimumSize = new Size(110, 36),
            Font = Font(8.25F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
        };

        void Apply()
        {
            var filled = primary && button.Enabled;
            button.BackColor = filled ? Accent : Background;
            button.ForeColor = filled ? Background : Text;
            button.FlatAppearance.BorderColor = filled ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = filled ? AccentHover : Surface;
            button.FlatAppearance.MouseDownBackColor = filled ? AccentHover : Border;
        }

        Apply();
        button.EnabledChanged += (_, _) => Apply();
        return button;
    }

    /// <summary>Draws a one-pixel border round <paramref name="control"/>.</summary>
    public static void PaintBorder(Control control, Graphics graphics)
    {
        using var pen = new Pen(Border);
        graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
    }

    /// <summary>Draws a border round <paramref name="table"/> and a divider between its rows.</summary>
    public static void PaintRows(TableLayoutPanel table, Graphics graphics)
    {
        PaintBorder(table, graphics);
        using var pen = new Pen(Border);
        var y = 0;
        var heights = table.GetRowHeights();
        for (var i = 0; i < heights.Length - 1; i++)
        {
            y += heights[i];
            if (heights[i] > 0)
            {
                graphics.DrawLine(pen, 0, y, table.Width, y);
            }
        }
    }

    /// <summary>Asks Windows for a dark title bar in the same colour as the window (ignored on older builds).</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        const int UseImmersiveDarkMode = 20;
        const int CaptionColor = 35;
        var on = 1;
        var color = ColorTranslator.ToWin32(Background);
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref color, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>A radio button drawn as a square box, filled with the accent colour when checked.</summary>
internal sealed class PixelRadio : RadioButton
{
    private const int BoxSize = 14;
    private const int Gap = 10;

    /// <summary>Space taken by the box, so text below can line up with the label.</summary>
    public const int TextIndent = BoxSize + Gap;

    /// <summary>Creates the radio button.</summary>
    public PixelRadio(string text)
    {
        Text = text;
        AutoSize = true;
        Font = Theme.Font(10.5F, FontStyle.Bold);
        ForeColor = Theme.Text;
        Margin = Padding.Empty;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <inheritdoc />
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(LogicalToDeviceUnits(TextIndent) + text.Width + 4, Math.Max(text.Height, LogicalToDeviceUnits(BoxSize)) + 6);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var box = LogicalToDeviceUnits(BoxSize);
        var stroke = Math.Max(1, LogicalToDeviceUnits(2));
        var top = (Height - box) / 2;
        using (var pen = new Pen(Checked ? Theme.Accent : Theme.Muted, stroke))
        {
            g.DrawRectangle(pen, stroke / 2, top + (stroke / 2), box - stroke, box - stroke);
        }

        if (Checked)
        {
            var inset = LogicalToDeviceUnits(4);
            using var fill = new SolidBrush(Theme.Accent);
            g.FillRectangle(fill, inset, top + inset, box - (2 * inset), box - (2 * inset));
        }

        var indent = LogicalToDeviceUnits(TextIndent);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(indent, 0, Width - indent, Height), ForeColor, flags);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(g, new Rectangle(indent - 2, 1, Width - indent + 1, Height - 2), Theme.Text, BackColor);
        }
    }
}

/// <summary>A bordered summary tile for the header: caption, large value, and a detail line.</summary>
internal sealed class StatusCard : TableLayoutPanel
{
    private readonly Label _value = Theme.Label(string.Empty, 15F, FontStyle.Bold);
    private readonly Label _detail = new()
    {
        AutoSize = false,
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Height = 18,
        Font = Theme.Font(8.25F),
        ForeColor = Theme.Muted,
        Margin = new Padding(0, 6, 0, 0),
    };

    /// <summary>Creates the card with a fixed <paramref name="caption"/>.</summary>
    public StatusCard(string caption)
    {
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        Dock = DockStyle.Fill;
        Padding = new Padding(14, 12, 14, 12);
        Margin = new Padding(4);

        var label = Theme.Label(caption, 8.25F, color: Theme.Muted);
        label.Margin = new Padding(0, 0, 0, 6);
        Controls.Add(label);
        Controls.Add(_value);
        Controls.Add(_detail);
    }

    /// <summary>Updates the value and detail line.</summary>
    public void Set(string value, Color color, string detail)
    {
        _value.Text = value;
        _value.ForeColor = color;
        _detail.Text = detail;
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Theme.PaintBorder(this, e.Graphics);
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Invalidate();
    }
}
