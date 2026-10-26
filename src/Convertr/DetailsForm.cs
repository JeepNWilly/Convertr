// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;

namespace Convertr;

/// <summary>Read-only details of one file (ffprobe facts and the plan). Opens with no text selected.</summary>
public sealed class DetailsForm : Form
{
    public DetailsForm(string title, string text)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 520);
        MinimumSize = new Size(500, 300);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        Icon = AppIcon.Load();

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f), Text = text,
        };
        var copy = new Button { Text = "Copy path", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(8, 2, 8, 2), DialogResult = DialogResult.OK };
        copy.Click += (_, _) =>
        {
            var first = text.Split('\n')[0].TrimEnd('\r');
            const string prefix = "File:       ";
            try { Clipboard.SetText(first.StartsWith(prefix) ? first[prefix.Length..] : first); } catch { /* clipboard busy */ }
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
        bar.Controls.Add(close);
        bar.Controls.Add(copy);
        Controls.Add(box);
        Controls.Add(bar);
        AcceptButton = close;
        CancelButton = close;
        ActiveControl = close;
        GridHelper.ClearTextSelection(this);
        Theming.Register(this);
        DialogPlacement.Attach(this);
    }
}
