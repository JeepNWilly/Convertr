// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;

namespace Convertr;

/// <summary>Small dialog to edit a list of lines (the junk-metadata patterns). Opens with nothing selected.</summary>
public sealed class TextEditForm : Form
{
    private readonly TextBox _box;
    public List<string> Lines => _box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public TextEditForm(string title, string help, IEnumerable<string> lines)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(640, 420);
        MinimumSize = new Size(420, 300);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        Icon = AppIcon.Load();
        var lbl = new Label { Text = help, Dock = DockStyle.Top, AutoSize = false, Height = 48, Padding = new Padding(8, 8, 8, 0) };
        _box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), Text = string.Join("\r\n", lines) };
        var ok = new Button { Text = "OK", AutoSize = true, Padding = new Padding(10, 2, 10, 2), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(10, 2, 10, 2), DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Reset to defaults", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        reset.Click += (_, _) => { _box.Text = string.Join("\r\n", Convertr.Core.Settings.DefaultJunkPatterns); _box.SelectionStart = 0; _box.SelectionLength = 0; };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(8) };
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        bar.Controls.Add(reset);
        Controls.Add(_box);
        Controls.Add(lbl);
        Controls.Add(bar);
        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = ok;
        GridHelper.ClearTextSelection(this);
        Theming.Register(this);
        DialogPlacement.Attach(this);
    }
}
