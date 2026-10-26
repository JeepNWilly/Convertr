// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr.Core;

namespace Convertr;

/// <summary>Where Convertr keeps its own files: next to the exe, or in %LOCALAPPDATA%\Convertr when that folder cannot be written (the logic lives in Core's platform paths).</summary>
internal static class AppPaths
{
    public static string DataDir => Convertr.Core.PlatformServices.Current.Paths.DataDir;
    public static string LogsDir => Convertr.Core.PlatformServices.Current.Paths.LogsDir;
    public static string QueuePath => Convertr.Core.PlatformServices.Current.Paths.QueuePath;
}

/// <summary>The summary shown before a run starts: counts, projected savings, where the originals go. Start or Cancel.</summary>
public sealed class ConfirmRunForm : Form
{
    private readonly CheckBox _dontAsk = new() { Text = "Do not show this summary again (not recommended)", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };

    public bool DontAskAgain => _dontAsk.Checked;

    public ConfirmRunForm(string heading, string body, bool offerDontAsk)
    {
        Text = "Convertr - start conversion?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        Icon = AppIcon.Load();
        AutoScaleMode = AutoScaleMode.Font;
        var head = new Label { Text = heading, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 8) };
        var text = new Label { Text = body, AutoSize = true, MaximumSize = new Size(640, 0) };
        var start = new Button { Text = "Start", AutoSize = true, Padding = new Padding(16, 3, 16, 3), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(16, 3, 16, 3), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(start);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 12) };
        layout.Controls.Add(head);
        layout.Controls.Add(text);
        if (offerDontAsk) layout.Controls.Add(_dontAsk);
        layout.Controls.Add(buttons);
        buttons.Width = 640;
        Controls.Add(layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = cancel;     // the safe choice is the default
        CancelButton = cancel;
        ActiveControl = cancel;
        Theming.Register(this);
        DialogPlacement.Attach(this);
    }
}

/// <summary>Undo window: lists converted files from the undo journals and restores the originals of the ones the user picks.</summary>
public sealed class UndoForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly List<UndoItem> _items;
    private readonly Button _undoSel = new() { Text = "Undo selected", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _undoAll = new() { Text = "Undo all that can be undone", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };

    /// <summary>The items the user asked to undo (empty when the window was just closed).</summary>
    public List<UndoItem> Chosen { get; } = new();

    public UndoForm(List<UndoItem> items)
    {
        _items = items;
        Text = "Convertr - undo conversions";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 520);
        MinimumSize = new Size(640, 320);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        Icon = AppIcon.Load();

        var help = new Label
        {
            Text = "Undo moves the converted file to the \"" + HoldingArea.UndoneFolderName + "\" folder (it is never deleted) and puts the original back with its old date. " +
                   "Select rows (Ctrl or Shift for several) and press Undo selected.",
            Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(8, 8, 8, 0),
        };
        var g = _grid;
        GridHelper.EnableDoubleBuffer(g);
        g.Dock = DockStyle.Fill;
        g.AutoGenerateColumns = false;
        g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false;
        g.ReadOnly = true;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = true;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 28;
        g.RowTemplate.Height = 22;
        void Col(string name, string header, int w, bool fill = false, bool right = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = w, MinimumWidth = 50, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None };
            if (right) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            g.Columns.Add(c);
        }
        Col("File", "Original file", 420, true);
        Col("Now", "Now", 70);
        Col("Orig", "Original", 90, right: true);
        Col("New", "Converted", 90, right: true);
        Col("Run", "Run", 110);
        Col("Status", "Status", 250);
        foreach (var it in items)
        {
            var rel = it.MediaFolder.Length > 0 ? HoldingArea.RelativeTo(it.MediaFolder, it.OriginalPath) : it.OriginalPath;
            g.Rows.Add(rel, Path.GetExtension(it.NewPath).TrimStart('.'), Mb(it.OrigSize), Mb(it.NewSize), it.RunId, it.Status);
        }
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _items.Count && !_items[e.RowIndex].CanUndo) e.CellStyle!.ForeColor = Theming.Colors.Muted;
        };

        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(10, 3, 10, 3), DialogResult = DialogResult.Cancel };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        bar.Controls.Add(close);
        bar.Controls.Add(_undoAll);
        bar.Controls.Add(_undoSel);
        _undoSel.Enabled = false;
        _undoAll.Enabled = items.Any(i => i.CanUndo);
        g.SelectionChanged += (_, _) => _undoSel.Enabled = g.SelectedRows.Cast<DataGridViewRow>().Any(r => _items[r.Index].CanUndo);
        _undoSel.Click += (_, _) => Finish(g.SelectedRows.Cast<DataGridViewRow>().Select(r => _items[r.Index]));
        _undoAll.Click += (_, _) => Finish(_items);
        Controls.Add(g);
        Controls.Add(help);
        Controls.Add(bar);
        CancelButton = close;
        ActiveControl = close;
        GridHelper.NoInitialSelection(g, this);
        Theming.Register(this);
        DialogPlacement.Attach(this);
    }

    private void Finish(IEnumerable<UndoItem> items)
    {
        var list = items.Where(i => i.CanUndo).ToList();
        if (list.Count == 0) return;
        var ok = MessageBox.Show(this, $"Restore {list.Count:N0} original file(s)?\r\n\r\nThe converted files are moved to the \"{HoldingArea.UndoneFolderName}\" folder, not deleted.",
            "Convertr", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (ok != DialogResult.OK) return;
        Chosen.AddRange(list);
        DialogResult = DialogResult.OK;
    }

    private static string Mb(long b) => b >= 1073741824 ? (b / 1073741824.0).ToString("0.00") + " GB" : (b / 1048576.0).ToString("0") + " MB";
}
