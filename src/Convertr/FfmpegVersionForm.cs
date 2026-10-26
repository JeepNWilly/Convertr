// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr.Core;

namespace Convertr;

/// <summary>
/// "Download older" (Setup): pick an older ffmpeg release. The built-in list shows at once, the online list (GitHub releases of the
/// gyan.dev builds) replaces it when it arrives. Nothing is selected until the user picks a row; Download stays disabled until then.
/// </summary>
public sealed class FfmpegVersionForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly Button _download = new() { Text = "Download", AutoSize = true, Padding = new Padding(14, 3, 14, 3), DialogResult = DialogResult.OK, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Padding = new Padding(14, 3, 14, 3), DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(10, 6, 10, 0), Tag = "muted" };
    private readonly HardwareProfile? _hw;
    private readonly Func<CancellationToken, Task<VersionLookup>> _lookup;
    private readonly CancellationTokenSource _cts = new();
    private List<FfmpegVersion> _items = new();

    /// <summary>The release the user picked (set when the dialog is closed with Download).</summary>
    public FfmpegVersion? Selected { get; private set; }

    public FfmpegVersionForm(HardwareProfile? hw, Func<CancellationToken, Task<VersionLookup>>? lookup = null)
    {
        _hw = hw;
        _lookup = lookup ?? (ct => FfmpegVersions.LookupAsync(ct));
        Text = "Download an older FFmpeg";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        SizeGripStyle = SizeGripStyle.Show;
        Size = new Size(1000, 440);
        MinimumSize = new Size(560, 300);
        Icon = AppIcon.Load();

        _status.Text = "Choose the version to download. The list is read from the official gyan.dev release builds on GitHub (essentials builds, ffmpeg.exe and ffprobe.exe only); a built-in list is shown first.";
        BuildGrid();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(10, 6, 10, 6) };
        bar.Controls.Add(_cancel);
        bar.Controls.Add(_download);
        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(bar);
        AcceptButton = _download;
        CancelButton = _cancel;
        ActiveControl = _cancel;

        Fill(FfmpegVersions.Fallback.ToList());
        _grid.SelectionChanged += (_, _) => _download.Enabled = _grid.SelectedRows.Count == 1;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _download.Enabled) _download.PerformClick(); };
        FormClosing += (_, _) =>
        {
            _cts.Cancel();
            Selected = DialogResult == DialogResult.OK && _grid.SelectedRows.Count == 1 ? _items[_grid.SelectedRows[0].Index] : null;
        };
        Shown += async (_, _) => await LoadOnlineAsync();
        GridHelper.NoInitialSelection(_grid, this);
        Theming.Register(this);
        DialogPlacement.Attach(this);
    }

    private void BuildGrid()
    {
        var g = _grid;
        GridHelper.EnableDoubleBuffer(g);
        g.Dock = DockStyle.Fill;
        g.AutoGenerateColumns = false;
        g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.ReadOnly = true; g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
        g.BackgroundColor = SystemColors.Window; g.BorderStyle = BorderStyle.FixedSingle;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.EnableHeadersVisualStyles = true;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 28;
        g.RowTemplate.Height = 40;
        g.ShowCellToolTips = true;
        void Col(string name, string header, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, MinimumWidth = 50, SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None };
            c.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.Columns.Add(c);
        }
        Col("Version", "Version", 70);
        Col("Released", "Released", 90);
        Col("Size", "Size", 70);
        Col("Fits", "With your GPU", 130);
        Col("Note", "Note", 400, true);
    }

    private void Fill(List<FfmpegVersion> items)
    {
        var keep = _grid.SelectedRows.Count == 1 && _grid.SelectedRows[0].Index < _items.Count ? _items[_grid.SelectedRows[0].Index].Version : null;
        _items = items;
        _grid.Rows.Clear();
        foreach (var v in items)
        {
            var n = FfmpegVersions.Describe(v.Version, _hw);
            var fits = n.Fit switch
            {
                DriverFit.Works => "Works with your driver",
                DriverFit.NeedsNewerDriver => $"No: needs driver {n.RequiredDriver?.Replace(".00", "")}",
                DriverFit.NoNvidia => "No NVIDIA GPU",
                _ => "Not known",
            };
            var i = _grid.Rows.Add(v.Version, v.Published?.ToString("yyyy-MM-dd") ?? "", v.SizeBytes is long b ? $"{b / 1048576.0:0} MB" : "", fits, n.Text);
            _grid.Rows[i].Cells["Note"].ToolTipText = n.Text;
        }
        GridHelper.Clear(_grid);
        if (keep != null)
        {
            var idx = items.FindIndex(x => x.Version == keep);
            if (idx >= 0) _grid.Rows[idx].Selected = true;
        }
        _download.Enabled = _grid.SelectedRows.Count == 1;
    }

    private async Task LoadOnlineAsync()
    {
        try
        {
            var shortStatus = _status.Text;
            _status.Text = "Reading the list of releases from GitHub...";
            var r = await _lookup(_cts.Token);
            if (IsDisposed) return;
            if (r.Online)
            {
                Fill(r.Versions.ToList());
                _status.Text = $"{r.Versions.Count} older releases from the official gyan.dev release builds on GitHub (essentials builds). Choose one, then press Download.";
            }
            else
            {
                _status.Text = $"Could not read the online list ({r.Error}). Showing the built-in list of versions instead. Choose one, then press Download.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "Could not read the online list (" + ex.Message + "). Showing the built-in list."; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
