// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Text;
using Convertr.Core;

namespace Convertr;

/// <summary>
/// Main window: pick the Media folder, Scan, review the plan, then Convert (queue, pause, stop, undo). Scanning never writes
/// to the library. Conversion writes next to each source file only through the verified swap (temporary file, verify, move
/// original to the holding folder, move the result into place) and logs everything.
/// </summary>
public sealed class MainForm : Form
{
    private Settings _settings;
    private readonly string _settingsPath;
    private readonly string _cachePath;
    private readonly ToolTip _tip = new() { AutoPopDelay = 12000, InitialDelay = 400 };

    private List<ScanEntry> _entries = new();
    private List<PlanRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _scanning;
    private bool _closeRequested;
    private bool _running;
    private ConversionEngine? _engine;
    private QueueFile? _queue;
    private readonly Dictionary<string, PlanRow> _rowByPath = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLogPath = "";
    private string? _sortProp;
    private SortOrder _sortOrder = SortOrder.None;

    private readonly Label _lblMedia = new() { Text = "Media", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
    private readonly TextBox _txtMedia = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 3, 6, 3), PlaceholderText = "Choose your Media folder (Browse...)" };
    private readonly Button _btnBrowse = new() { Text = "Browse...", AutoSize = true, Padding = new Padding(8, 1, 8, 1), Margin = new Padding(3, 2, 3, 2) };
    private readonly TableLayoutPanel _libTable = new() { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0) };

    private readonly RadioButton _rbGpu = new() { Text = "GPU (NVENC, fast)", AutoSize = true };
    private readonly RadioButton _rbCpu = new() { Text = "CPU (x265, smaller)", AutoSize = true };
    private readonly Button _btnSetup = Btn("Setup...");
    private readonly Button _btnScan = Btn("Scan");
    private readonly Button _btnCancel = Btn("Cancel");
    private readonly Button _btnConvert = Btn("Convert");
    private readonly Button _btnPause = Btn("Pause");
    private readonly Button _btnStop = Btn("Stop");
    private readonly Button _btnStopAfter = Btn("Stop after this file");
    private readonly Button _btnUndo = Btn("Undo...");
    private readonly Button _btnPurge = Btn("Purge Originals...");
    private readonly Button _btnExport = Btn("Export plan CSV...");
    private readonly CheckBox _chkDry = new() { Text = "Dry run (log the commands, change nothing)", AutoSize = true, Margin = new Padding(16, 3, 0, 0) };
    private readonly FlowLayoutPanel _buttonBar = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };

    private readonly ComboBox _fAction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ComboBox _fRes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly TextBox _fSearch = new() { Width = 200 };
    private readonly CheckBox _fWarn = new() { Text = "Warnings only", AutoSize = true };
    private readonly Label _lblShowing = new() { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "muted" };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };

    private readonly DataGridView _grid = new();
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 18, Minimum = 0, Maximum = 100, Margin = new Padding(0, 3, 8, 3) };
    private readonly Label _lblProgress = new() { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(900, 0) };
    private TableLayoutPanel _root = null!;
    private readonly Label _lblSummary = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(2, 2, 2, 0), MaximumSize = new Size(2000, 0) };
    private readonly Label _lblSavings = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(2, 0, 2, 2), Tag = "good", MaximumSize = new Size(2000, 0) };

    private static Button Btn(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 8, 0) };

    private static string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            return $"v{v.Major}.{v.Minor}.{v.Build} (build {v.Revision})";
        }
    }

    public DataGridView Grid => _grid;
    public Settings CurrentSettings => _settings;

    /// <summary>The test harness turns the start-up hardware check off so screenshots and tests do not change settings.</summary>
    public static bool DisableHardwareAutoDetect { get; set; }
    private readonly List<string> _startupNotes;
    private bool _hwChecking;


    public MainForm() : this(null, null) { }

    /// <param name="settingsPath">settings.json location (default: next to the exe).</param>
    /// <param name="cachePath">probe_cache.json location (default: next to the exe).</param>
    public MainForm(string? settingsPath, string? cachePath)
    {
        _settingsPath = settingsPath ?? SettingsStore.DefaultPath;
        _cachePath = cachePath ?? Path.Combine(AppContext.BaseDirectory, "probe_cache.json");
        _settings = SettingsStore.Load(_settingsPath);
        // paths and measurements that belong to another computer are cleared in memory (the settings file itself is never deleted)
        _startupNotes = SettingsValidator.ApplyForThisPc(_settings, Environment.MachineName, Directory.Exists,
            f => File.Exists(Path.Combine(f, "ffprobe.exe")) || File.Exists(Path.Combine(f, "ffprobe")));
        Theming.SetMode(_settings.Theme);

        Text = $"Convertr {VersionText}";
        Icon = AppIcon.Load();
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1380, 800);
        MinimumSize = new Size(560, 420);

        BuildLayout();
        BuildGrid();
        WireEvents();
        _txtMedia.Text = _settings.MediaFolder;
        _chkDry.Checked = _settings.DryRun;
        ApplyModeToRadios();
        PopulateFilters();
        SetBusy(false);
        UpdateSummary();
        _lblProgress.Text = _settings.MediaFolder.Length == 0
            ? "Choose your Media folder (Browse...), then press Scan. Scanning is read-only; nothing is changed."
            : "Not scanned yet. Press Scan to read the Media folder (read-only; nothing is changed).";
        if (_startupNotes.Count > 0)
        {
            _lblProgress.Text = string.Join(" ", _startupNotes);
            try { SettingsStore.Save(_settings, _settingsPath); } catch { /* read-only folder: the notice is shown again next time */ }
        }
        GridHelper.NoInitialSelection(_grid, this);
        GridHelper.ClearTextSelection(this);
        Shown += (_, _) => { ActiveControl = _btnScan; if (!DisableHardwareAutoDetect) _ = CheckHardwareAsync(); };
        Theming.Track(_tip);
        Theming.Register(this);
    }

    // ================= layout =================
    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10, 8, 10, 6) };
        _root = root;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++) root.RowStyles.Add(i == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));

        _libTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _libTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _libTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _libTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _libTable.Controls.Add(_lblMedia, 0, 0);
        _libTable.Controls.Add(_txtMedia, 1, 0);
        _libTable.Controls.Add(_btnBrowse, 2, 0);
        root.Controls.Add(_libTable, 0, 0);

        var modeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 4, 0, 2) };
        modeBar.Controls.Add(new Label { Text = "Mode (sets the savings estimate):", AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
        modeBar.Controls.Add(_rbGpu);
        modeBar.Controls.Add(_rbCpu);
        _btnSetup.Margin = new Padding(24, 0, 8, 0);
        modeBar.Controls.Add(_btnSetup);
        modeBar.Controls.Add(_chkDry);
        root.Controls.Add(modeBar, 0, 1);

        _buttonBar.Controls.AddRange(new Control[] { _btnScan, _btnCancel, _btnConvert, _btnPause, _btnStop, _btnStopAfter, _btnUndo, _btnPurge, _btnExport });
        root.Controls.Add(_buttonBar, 0, 2);

        var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 6, 0, 4) };
        filter.Controls.Add(new Label { Text = "Filter:  Action", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
        filter.Controls.Add(_fAction);
        filter.Controls.Add(new Label { Text = "Resolution", AutoSize = true, Margin = new Padding(12, 5, 4, 0) });
        filter.Controls.Add(_fRes);
        filter.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(12, 5, 4, 0) });
        filter.Controls.Add(_fSearch);
        _fWarn.Margin = new Padding(12, 4, 0, 0);
        filter.Controls.Add(_fWarn);
        _lblShowing.Margin = new Padding(18, 5, 0, 0);
        filter.Controls.Add(_lblShowing);
        root.Controls.Add(filter, 0, 3);

        _grid.Dock = DockStyle.Fill;
        root.Controls.Add(_grid, 0, 4);

        var prog = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        prog.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        prog.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        prog.Controls.Add(_progress, 0, 0);
        prog.Controls.Add(_lblProgress, 1, 0);
        root.Controls.Add(prog, 0, 5);

        var sum = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        sum.Controls.Add(_lblSummary, 0, 0);
        sum.Controls.Add(_lblSavings, 0, 1);
        root.Controls.Add(sum, 0, 6);

        Controls.Add(root);

        // The status lines wrap onto more lines when the window gets narrower (instead of being cut off):
        // their maximum width follows the window, the rows are auto-height and the grid row takes what is left.
        root.SizeChanged += (_, _) => UpdateWrapWidths();
        UpdateWrapWidths();

        _btnPurge.Enabled = false;
        GridHelper.TipForDisabled(_buttonBar, _tip,
            (_btnConvert, "Scan first. Converts the selected rows, or the rows shown when none are selected."), (_btnPause, "Nothing is running"), (_btnStop, "Nothing is running"),
            (_btnStopAfter, "Nothing is running"), (_btnUndo, "No conversions to undo yet"), (_btnPurge, "Purge Originals: coming in M3"),
            (_btnCancel, "Cancel stops a running scan"), (_btnExport, "Run a scan first"));
        _tip.SetToolTip(_btnConvert, "Converts the selected rows (or all rows shown when none are selected). Only rows with an action that writes a file are converted.");
        _tip.SetToolTip(_btnPause, "Suspends ffmpeg right now; press again to continue where it stopped.");
        _tip.SetToolTip(_btnStop, "Stops now: the running ffmpeg is ended, its partial file deleted and the original left alone. The rest stays queued and can be resumed.");
        _tip.SetToolTip(_btnStopAfter, "Finishes the file that is running, then stops. Nothing is lost.");
        _tip.SetToolTip(_btnUndo, "Restore originals from the undo journals (the converted files are moved aside, never deleted).");
        _tip.SetToolTip(_chkDry, "Builds and logs the exact ffmpeg command for every file but never starts ffmpeg or touches any file.");
        _tip.SetToolTip(_txtMedia, "The folder to scan. All video files below it are read (recursively). Scan one group at a time, for example Movies or Series.");
        _tip.SetToolTip(_btnBrowse, "Choose the Media folder to scan");
        _tip.SetToolTip(_btnScan, "Reads every video with ffprobe (read-only) and builds the plan. Unchanged files come from the cache.");
        _tip.SetToolTip(_btnSetup, "Thresholds, subtitles, metadata, FFmpeg location and the other options.");
        _tip.SetToolTip(_rbGpu, "GPU (NVENC) estimate: 20-35% saved on size candidates.");
        _tip.SetToolTip(_rbCpu, "CPU (x265) estimate: 30-40% saved on size candidates.");
        _tip.SetToolTip(_lblSavings, "Before a run these are estimates. After a run the measured result is shown here.");
    }

    /// <summary>Keeps the progress, summary and green savings lines as wide as the window so they wrap instead of clipping.</summary>
    private void UpdateWrapWidths()
    {
        if (_root == null) return;
        var w = Math.Max(240, _root.ClientSize.Width - _root.Padding.Horizontal - 4);
        _lblSummary.MaximumSize = new Size(w, 0);
        _lblSavings.MaximumSize = new Size(w, 0);
        _lblProgress.MaximumSize = new Size(Math.Max(160, w - 420 - 12), 0);
    }

    private void BuildGrid()
    {
        var g = _grid;
        GridHelper.EnableDoubleBuffer(g);
        g.AutoGenerateColumns = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.ReadOnly = true;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = true;
        g.BackgroundColor = SystemColors.Window;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Color.FromArgb(230, 230, 230);
        g.EnableHeadersVisualStyles = true;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 28;
        g.RowTemplate.Height = 22;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.ShowCellToolTips = true;

        void Col(string prop, string header, int width, bool fill = false, float weight = 100, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = prop, DataPropertyName = prop, HeaderText = header, Width = width, MinimumWidth = 40, SortMode = DataGridViewColumnSortMode.Automatic,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, FillWeight = weight,
            };
            c.DefaultCellStyle.Alignment = align;
            g.Columns.Add(c);
        }

        Col(nameof(PlanRow.File), "File", 300, true, 40);
        Col(nameof(PlanRow.Container), "Container", 70);
        Col(nameof(PlanRow.VideoCodec), "Video codec", 92);
        Col(nameof(PlanRow.Resolution), "Resolution", 76);
        Col(nameof(PlanRow.VideoKbps), "Video kbps", 76, align: DataGridViewContentAlignment.MiddleRight);
        Col(nameof(PlanRow.Audio), "Audio", 118);
        Col(nameof(PlanRow.Metadata), "Metadata", 190);
        Col(nameof(PlanRow.Action), "Action", 110);
        Col(nameof(PlanRow.Status), "Status", 150);
        Col(nameof(PlanRow.Reason), "Reason", 300, true, 45);
        Col(nameof(PlanRow.EstSaveMidBytes), "Est. savings", 112, align: DataGridViewContentAlignment.MiddleRight);

        g.CellFormatting += OnCellFormatting;
        g.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= g.Rows.Count || g.Rows[e.RowIndex].DataBoundItem is not PlanRow r) return;
            var name = g.Columns[e.ColumnIndex].Name;
            e.ToolTipText = name switch
            {
                nameof(PlanRow.File) => r.Entry.FullPath,
                nameof(PlanRow.Reason) => r.Reason,
                nameof(PlanRow.VideoKbps) => r.Entry.Probe.Video?.KbpsSource is { Length: > 0 } s ? "Bitrate source: " + s + (s == "estimated" ? " (overall bitrate minus audio)" : "") : "",
                nameof(PlanRow.Metadata) => r.Plan.Metadata.Summary,
                nameof(PlanRow.Resolution) => r.Entry.Probe.Video is { } v ? $"{v.Width}x{v.Height}" : "",
                nameof(PlanRow.Audio) => string.Join("\n", r.Entry.Probe.Audio.Select(a => a.Language + " " + a.Describe())),
                _ => "",
            };
        };
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || g.Rows[e.RowIndex].DataBoundItem is not PlanRow r) return;
            using var f = new DetailsForm(r.Entry.FileName, r.Details(_settings, FfmpegLocator.Find(_settings.FfmpegFolder) is { FfmpegPath.Length: > 0 } l ? l.FfmpegPath : "ffmpeg"));
            f.ShowDialog(this);
        };
        g.Sorted += (_, _) =>
        {
            _sortProp = g.SortedColumn?.DataPropertyName;
            _sortOrder = g.SortOrder;
        };
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count || _grid.Rows[e.RowIndex].DataBoundItem is not PlanRow r) return;
        var name = _grid.Columns[e.ColumnIndex].Name;
        if (name == nameof(PlanRow.VideoKbps)) { e.Value = r.KbpsText; e.FormattingApplied = true; }
        else if (name == nameof(PlanRow.EstSaveMidBytes)) { e.Value = r.EstSavings; e.FormattingApplied = true; }

        if (name == nameof(PlanRow.Status) && r.JobState is { } js)
            e.CellStyle!.ForeColor = js switch { JobState.Done => Theming.Colors.Good, JobState.Failed => Theming.Colors.Bad, JobState.Pending or JobState.NotWorthIt or JobState.Skipped => Theming.Colors.Muted, _ => e.CellStyle!.ForeColor };
        if (r.Plan.Action == PlanAction.Error) e.CellStyle!.ForeColor = Theming.ErrorRowColor;
        else if (name == nameof(PlanRow.Action)) e.CellStyle!.ForeColor = Theming.ActionColor(r.Plan.Action);
    }

    // ================= events =================
    private void WireEvents()
    {
        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); _lblProgress.Text = "Cancelling..."; };
        _btnSetup.Click += (_, _) => OpenSetup();
        _btnConvert.Click += async (_, _) => await StartConversionAsync();
        _btnPause.Click += (_, _) => TogglePause();
        _btnStop.Click += (_, _) => StopNow();
        _btnStopAfter.Click += (_, _) => { _engine?.StopAfterCurrent(); _btnStopAfter.Enabled = false; _lblProgress.Text = "Finishing the current file, then stopping..."; };
        _btnUndo.Click += (_, _) => OpenUndo();
        _chkDry.CheckedChanged += (_, _) => { _settings.DryRun = _chkDry.Checked; SaveSettings(quiet: true); };
        Shown += async (_, _) => await OfferResumeAsync();
        _btnBrowse.Click += (_, _) => BrowseMedia();
        _btnExport.Click += (_, _) => ExportCsv();
        _rbGpu.CheckedChanged += (_, _) => OnModeChanged();
        _rbCpu.CheckedChanged += (_, _) => OnModeChanged();
        _fAction.SelectedIndexChanged += (_, _) => ApplyFilters();
        _fRes.SelectedIndexChanged += (_, _) => ApplyFilters();
        _fWarn.CheckedChanged += (_, _) => ApplyFilters();
        _fSearch.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilters(); };
        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_running)
        {
            var ans = MessageBox.Show(this, "A conversion is running.\r\n\r\nStop it and close? The file being converted is abandoned (its partial output is deleted, the original is untouched). The rest stays queued and can be resumed next time.",
                "Convertr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            e.Cancel = true;
            if (ans == DialogResult.Yes) { _closeRequested = true; _engine?.Stop(); _lblProgress.Text = "Stopping, then closing..."; }
            return;
        }
        if (_scanning)
        {
            _closeRequested = true;
            _cts?.Cancel();
            e.Cancel = true;
            _lblProgress.Text = "Stopping the scan, then closing...";
            return;
        }
        SaveSettings(quiet: true);
    }

    /// <summary>
    /// At start-up: detects the hardware in the background when there is no saved profile, or when the GPU, its driver or ffmpeg changed.
    /// Defaults are applied only for settings the user did not change by hand. Never blocks the window; the result is shown in the status line.
    /// </summary>
    private async Task CheckHardwareAsync()
    {
        if (_hwChecking) return;
        _hwChecking = true;
        try
        {
            var loc = FfmpegLocator.Find(_settings.FfmpegFolder);
            var saved = _settings.Hardware;
            string? why = null;
            if (saved == null) why = "first launch";
            else if (await Task.Run(() => HardwareDetector.HasChangedAsync(saved, loc, new ProcessHardwareProbe()))) why = "the computer, graphics card, driver or ffmpeg changed";
            if (why == null) return;
            Notice(why == "first launch" ? "Detecting your hardware to choose encoder defaults..." : $"Detecting hardware again ({why})...");
            var hw = await Task.Run(() => HardwareDetector.DetectAsync(loc));
            if (IsDisposed) return;
            if (saved != null && (!string.Equals(saved.Cpu.Name, hw.Cpu.Name, StringComparison.OrdinalIgnoreCase) ||
                                  (saved.MachineName.Length > 0 && !string.Equals(saved.MachineName, hw.MachineName, StringComparison.OrdinalIgnoreCase))))
            { _settings.BenchmarkCpuX = null; _settings.BenchmarkGpuX = null; _settings.BenchmarkUtc = null; }   // speeds measured on another computer
            var d = HardwareDefaults.Decide(hw, _settings.DefaultsPolicy);
            var applied = HardwareDefaults.Apply(_settings, d, hw);
            SaveSettings(quiet: true);
            ApplyModeToRadios();
            if (_entries.Count > 0 && applied.Count > 0 && !_running) Replan();
            Notice($"Hardware detected: {hw.Cpu.Name}, {(hw.Gpus.Count > 0 ? hw.Gpus[0].Name : "no GPU")}. Defaults: {hw.DefaultsSummary}. Details are in Setup > Hardware." +
                   (applied.Count == 0 ? " (Nothing needed changing.)" : ""));
        }
        catch (Exception ex) { Notice("Hardware detection did not finish: " + ex.Message); }
        finally { _hwChecking = false; }
    }

    private void Notice(string text)
    {
        if (IsDisposed) return;
        if (_scanning || _running) return;   // never overwrite live progress; the result is in Setup > Hardware
        _lblProgress.Text = text;
    }

    private bool _applyingMode;

    private void OnModeChanged()
    {
        if (_applyingMode) return;   // radios set by code (start-up, detection) are not a choice of the user
        var mode = _rbCpu.Checked ? EncoderMode.Cpu : EncoderMode.Gpu;
        if (mode == _settings.Mode) return;
        _settings.Mode = mode;
        if (!_settings.UserModified.Contains(TunedSettings.Mode)) _settings.UserModified.Add(TunedSettings.Mode);   // the user chose it: detection keeps it
        SaveSettings(quiet: true);
        if (_entries.Count > 0) Replan();
    }

    private void ApplyModeToRadios()
    {
        _applyingMode = true;
        try
        {
            _rbGpu.Checked = _settings.Mode == EncoderMode.Gpu;
            _rbCpu.Checked = _settings.Mode == EncoderMode.Cpu;
        }
        finally { _applyingMode = false; }
    }

    private void OpenSetup()
    {
        CollectFolders();
        using var f = new SetupForm(_settings) { QueueEntries = RowsToConvert().Select(r => r.Entry).ToList() };
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _settings = f.Result;
        Theming.SetMode(_settings.Theme);
        SaveSettings(quiet: false);
        ApplyModeToRadios();
        if (_entries.Count > 0) Replan();
    }

    // ================= media folder =================
    private void BrowseMedia()
    {
        using var d = new FolderBrowserDialog { Description = "Choose the Media folder to scan", UseDescriptionForTitle = true, SelectedPath = _txtMedia.Text.Trim() };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _txtMedia.Text = d.SelectedPath;
        SaveSettings(quiet: true);
    }

    private void CollectFolders() => _settings.MediaFolder = _txtMedia.Text.Trim();

    /// <summary>Folder name used as the group label (for example "Movies"); a drive root uses the whole path.</summary>
    private static string GroupLabel(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var leaf = System.IO.Path.GetFileName(trimmed);
        return leaf.Length > 0 ? leaf : path;
    }

    private void SaveSettings(bool quiet)
    {
        try
        {
            CollectFolders();
            SettingsStore.Save(_settings, _settingsPath);
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(this, "Could not save settings.json:\r\n" + ex.Message, "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================= scan =================
    private void SetBusy(bool busy)
    {
        _scanning = busy;
        UpdateButtons();
    }

    /// <summary>Enables each button for the current state: idle, scanning or converting.</summary>
    private void UpdateButtons()
    {
        var idle = !_scanning && !_running;
        _btnScan.Enabled = idle;
        _btnCancel.Enabled = _scanning;
        _btnSetup.Enabled = idle;
        _txtMedia.Enabled = _btnBrowse.Enabled = idle;
        _btnExport.Enabled = idle && _rows.Count > 0;
        _rbGpu.Enabled = _rbCpu.Enabled = idle;
        _chkDry.Enabled = idle;
        _btnConvert.Enabled = idle && _rows.Any(r => r.Plan.WritesFile);
        _btnPause.Enabled = _running;
        _btnStop.Enabled = _running;
        _btnStopAfter.Enabled = _running;
        _btnUndo.Enabled = idle && HasUndoJournals();
        if (!_running) _btnPause.Text = "Pause";
    }

    private static bool HasUndoJournals()
    {
        try { return Directory.Exists(AppPaths.LogsDir) && Directory.GetFiles(AppPaths.LogsDir, "undo_*.csv").Length > 0; } catch { return false; }
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        if (_txtMedia.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Choose the Media folder first (Browse...).", "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SaveSettings(quiet: false);
        var loc = FfmpegLocator.Find(_settings.FfmpegFolder);
        if (loc == null)
        {
            if (MessageBox.Show(this, "ffprobe was not found (looked next to Convertr, in its ffmpeg folder, in the download folder and on PATH).\r\n\r\nOpen Setup to choose the folder that contains ffmpeg.exe and ffprobe.exe?",
                    "Convertr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) OpenSetup();
            return;
        }
        var roots = new List<LibraryRoot> { new(GroupLabel(_settings.MediaFolder), _settings.MediaFolder) };

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _progress.Style = ProgressBarStyle.Marquee;
        _lblProgress.Text = "Listing files...";
        var progress = new Progress<ScanProgress>(OnScanProgress);
        ScanResult? result = null;
        try
        {
            var cache = ProbeCache.Load(_cachePath);
            var scanner = new Scanner(new FfprobeRunner(loc.FfprobePath), cache, _settings.ProbeThreads, _settings.IncludePlexVersions);
            result = await scanner.ScanAsync(roots, progress, _cts.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The scan failed:\r\n" + ex.Message, "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _cts.Dispose();
            _cts = null;
        }

        if (result != null)
        {
            _entries = result.Entries;
            SetBusy(false);
            Replan();
            var t = result.Elapsed;
            var took = t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.0}s";
            var msg = result.Cancelled
                ? $"Scan cancelled after {took}: showing the {result.Entries.Count:N0} files read so far. The cache keeps them, so the next scan resumes."
                : $"Scan finished in {took}: {result.Entries.Count:N0} files ({result.CacheHits:N0} from cache, {result.Probed:N0} probed, {result.Failed:N0} unreadable). Nothing was changed.";
            if (result.MissingRoots.Count > 0) msg += "  Folder not found: " + string.Join(", ", result.MissingRoots);
            _progress.Value = _progress.Maximum;
            _lblProgress.Text = msg;
        }
        else SetBusy(false);

        if (_closeRequested) Close();
    }

    private void OnScanProgress(ScanProgress p)
    {
        if (p.Phase == ScanPhase.Enumerating) { _progress.Style = ProgressBarStyle.Marquee; _lblProgress.Text = "Listing files..."; return; }
        if (_progress.Style != ProgressBarStyle.Continuous) _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = Math.Max(1, p.Total);
        _progress.Value = Math.Min(p.Done, _progress.Maximum);
        if (p.Phase == ScanPhase.Probing)
            _lblProgress.Text = $"Probing {p.Done:N0} of {p.Total:N0}  ({p.FromCache:N0} from cache{(p.Failed > 0 ? ", " + p.Failed + " unreadable" : "")})   {p.Current}";
    }

    /// <summary>Shows entries as if they had just been scanned (used by the screenshot harness and tests).</summary>
    public void LoadEntries(IEnumerable<ScanEntry> entries, string status)
    {
        _entries = entries.ToList();
        Replan();
        _lblProgress.Text = status;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 100;
        _progress.Value = 100;
    }

    // ================= plan, filters, summary =================
    private void Replan()
    {
        var opts = _settings.ToPlannerOptions();
        _rows = _entries.Select(e => new PlanRow(e, Planner.Plan(e, opts))).ToList();
        UpdateButtons();
        PopulateFilters();
        UpdateSummary();
        ApplyFilters();
    }

    private static readonly string[] ActionFilters =
    {
        "All actions", "Re-encode video (all)", "   Re-encode: size (H.264 to HEVC)", "   Re-encode: compatibility (legacy)",
        "Fix audio only", "Remux", "Skip", "Review", "Error",
    };

    private void PopulateFilters()
    {
        var a = _fAction.SelectedIndex;
        var r = _fRes.Text;
        _fAction.Items.Clear(); _fAction.Items.AddRange(ActionFilters);
        _fRes.Items.Clear(); _fRes.Items.Add("All resolutions");
        var order = new[] { "4K", "1080p", "720p", "576p", "480p", "Below 480p", "?" };
        _fRes.Items.AddRange(_rows.Select(x => x.Resolution).Distinct().OrderBy(x => Array.IndexOf(order, x)).Cast<object>().ToArray());
        _suppress = true;
        _fAction.SelectedIndex = a >= 0 ? a : 0;
        _fRes.SelectedIndex = Math.Max(0, _fRes.Items.IndexOf(r));
        _suppress = false;
    }

    private bool _suppress;

    private void ApplyFilters()
    {
        if (_suppress) return;
        IEnumerable<PlanRow> q = _rows;
        switch (_fAction.SelectedIndex)
        {
            case 1: q = q.Where(x => x.Plan.Action == PlanAction.ReencodeVideo); break;
            case 2: q = q.Where(x => x.Plan.Action == PlanAction.ReencodeVideo && x.Plan.Kind == ReencodeKind.Size); break;
            case 3: q = q.Where(x => x.Plan.Action == PlanAction.ReencodeVideo && x.Plan.Kind == ReencodeKind.Compat); break;
            case 4: q = q.Where(x => x.Plan.Action == PlanAction.FixAudioOnly); break;
            case 5: q = q.Where(x => x.Plan.Action == PlanAction.Remux); break;
            case 6: q = q.Where(x => x.Plan.Action == PlanAction.Skip); break;
            case 7: q = q.Where(x => x.Plan.Action == PlanAction.Review); break;
            case 8: q = q.Where(x => x.Plan.Action == PlanAction.Error); break;
        }
        if (_fRes.SelectedIndex > 0) { var res = _fRes.Text; q = q.Where(x => x.Resolution == res); }
        if (_fWarn.Checked) q = q.Where(x => x.Plan.HasWarnings);
        var text = _fSearch.Text.Trim();
        if (text.Length > 0)
            q = q.Where(x => x.File.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Reason.Contains(text, StringComparison.OrdinalIgnoreCase)
                             || x.Audio.Contains(text, StringComparison.OrdinalIgnoreCase) || x.VideoCodec.Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        _grid.DataSource = new SortableBindingList<PlanRow>(list);
        if (_sortProp != null && _sortOrder != SortOrder.None)
        {
            var col = _grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == _sortProp);
            if (col != null) _grid.Sort(col, _sortOrder == SortOrder.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending);
        }
        _lblShowing.Text = _rows.Count == 0 ? "" : $"Showing {list.Count:N0} of {_rows.Count:N0}";
        GridHelper.Clear(_grid);
    }

    private void UpdateSummary()
    {
        if (_rows.Count == 0)
        {
            _lblSummary.Text = "No results yet.";
            _lblSavings.Text = "";
            return;
        }
        var s = PlanSummary.From(_rows.Select(r => (r.Entry, r.Plan)));
        var tb = s.TotalBytes / 1099511627776.0;
        var total = tb >= 1 ? tb.ToString("0.00", CultureInfo.InvariantCulture) + " TB" : (s.TotalBytes / 1073741824.0).ToString("0", CultureInfo.InvariantCulture) + " GB";
        _lblSummary.Text =
            $"{s.Total:N0} scanned ({total})   |   Re-encode (size) {s.ReencodeSize:N0}   |   Re-encode (compat) {s.ReencodeCompat:N0}   |   Fix audio only {s.FixAudio:N0}   |   Remux {s.Remux:N0}   |   Skip {s.Skip:N0}   |   Review {s.Review:N0}   |   Error {s.Error:N0}" +
            (s.Warnings > 0 ? $"   |   {s.Warnings:N0} with ffprobe warnings" : "");
        var mode = _settings.Mode == EncoderMode.Gpu ? "GPU" : "CPU";
        _lblSavings.Text =
            $"Projected saving: ~{PlanSummary.Gb(s.EstSaveLowBytes)} - {PlanSummary.Gb(s.EstSaveHighBytes)}, most likely ~{PlanSummary.Gb(s.EstSaveMidBytes)} (ESTIMATE from measured results, {mode} mode, {s.ReencodeSize:N0} size candidates, {PlanSummary.Gb(s.SizeCandidateBytes)}; " +
            (s.LikelyRefused > 0 ? $"{s.LikelyRefused:N0} of them ({PlanSummary.Gb(s.LikelyRefusedBytes)}) are already lean and would probably be refused by the keep-if-smaller rule, so they count as 0; " : "") +
            "not a promise).   " +
            $"{s.ReencodeCompat:N0} legacy files are converted for playback, not space.   {s.AudioConversions:N0} files also convert audio.   * after a bitrate = estimated from the overall bitrate.";
    }

    // ================= convert =================
    private List<PlanRow> RowsToConvert()
    {
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem as PlanRow).Where(r => r != null).Cast<PlanRow>().ToList();
        var shown = _grid.Rows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem as PlanRow).Where(r => r != null).Cast<PlanRow>().ToList();
        var src = selected.Count > 0 ? selected : shown;
        return src.Where(r => r.Plan.WritesFile && r.Entry.Probe.Video != null && r.JobState is null or JobState.Pending or JobState.Failed or JobState.Skipped).ToList();
    }

    private static string Gb(long bytes) => PlanSummary.Gb(bytes);

    private async Task StartConversionAsync()
    {
        if (_scanning || _running) return;
        CollectFolders();
        var work = RowsToConvert();
        if (work.Count == 0)
        {
            MessageBox.Show(this, "There is nothing to convert in the rows you selected or the rows shown.\r\n\r\nOnly rows whose action writes a file (Re-encode, Fix audio only, Remux) are converted. Skip, Review and Error rows are never touched.",
                "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var loc = FfmpegLocator.Find(_settings.FfmpegFolder);
        if (loc == null || loc.FfmpegPath.Length == 0)
        {
            if (MessageBox.Show(this, "ffmpeg.exe was not found.\r\n\r\nOpen Setup to download it or choose its folder?", "Convertr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) OpenSetup();
            return;
        }
        if (await OfferExistingQueueAsync()) return;

        if (_settings.Mode == EncoderMode.Gpu && !_settings.DryRun && work.Any(r => r.Plan.Action == PlanAction.ReencodeVideo))
        {
            _lblProgress.Text = "Testing GPU encoding (NVENC)...";
            var nv = await FfmpegLocator.TestNvencAsync(loc);
            if (!nv.Ok)
            {
                var ans = MessageBox.Show(this, "GPU encoding (NVENC) does not work here:\r\n\r\n" + nv.Message + "\r\n\r\nSwitch to CPU mode (x265) for this run?", "Convertr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) { _lblProgress.Text = "Conversion not started (GPU encoding is not available)."; return; }
                _settings.Mode = EncoderMode.Cpu; ApplyModeToRadios(); SaveSettings(true); Replan();
                work = RowsToConvert();
            }
        }

        var runId = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var q = QueueBuilder.Build(work.Select(r => (r.Entry, r.Plan)), _settings, _settings.MediaFolder, runId);
        var startable = q.Jobs.Where(j => !j.IsFinal).ToList();
        foreach (var j in q.Jobs.Where(j => j.IsFinal)) if (_rowByPathTry(j.Entry.FullPath) is { } row) { row.JobState = j.State; row.Status = j.Message; }
        if (startable.Count == 0)
        {
            _grid.Invalidate();
            MessageBox.Show(this, "No file can be started:\r\n\r\n" + string.Join("\r\n", q.Jobs.Take(8).Select(j => j.Entry.FileName + ": " + j.Message)), "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_settings.DryRun)
        {
            var first = !_settings.FirstRunConfirmed;
            if (first || !_settings.SkipRunSummary)
            {
                var (heading, body) = BuildRunSummary(q, startable);
                using var dlg = new ConfirmRunForm(heading, body, offerDontAsk: !first);
                if (dlg.ShowDialog(this) != DialogResult.OK) { _lblProgress.Text = "Conversion cancelled. Nothing was changed."; return; }
                _settings.FirstRunConfirmed = true;
                if (dlg.DontAskAgain) _settings.SkipRunSummary = true;
                SaveSettings(true);
            }
        }
        await RunQueueAsync(q, loc);
    }

    private PlanRow? _rowByPathTry(string path) => _rowByPath.TryGetValue(path, out var r) ? r : null;

    private (string Heading, string Body) BuildRunSummary(QueueFile q, List<QueueJob> jobs)
    {
        var s = _settings;
        var bytes = jobs.Sum(j => j.Entry.SizeBytes);
        var sb = new StringBuilder();
        int Count(PlanAction a, ReencodeKind? k = null) => jobs.Count(j => j.Plan.Action == a && (k == null || j.Plan.Kind == k));
        sb.AppendLine($"Re-encode (size) {Count(PlanAction.ReencodeVideo, ReencodeKind.Size):N0}   |   Re-encode (compat) {Count(PlanAction.ReencodeVideo, ReencodeKind.Compat):N0}   |   Fix audio only {Count(PlanAction.FixAudioOnly):N0}   |   Remux {Count(PlanAction.Remux):N0}");
        var deint = jobs.Count(j => j.Plan.Deinterlace);
        if (deint > 0) sb.AppendLine($"{deint:N0} interlaced file(s) will be deinterlaced (bwdif).");
        sb.AppendLine();
        var low = jobs.Sum(j => j.Plan.EstSaveLowBytes);
        var high = jobs.Sum(j => j.Plan.EstSaveHighBytes);
        var mid = jobs.Sum(j => j.Plan.EstSaveMidBytes);
        var refused = jobs.Count(j => j.Plan.Kind == ReencodeKind.Size && j.Plan.LikelyRefused);
        sb.AppendLine($"Projected saving: ~{Gb(low)} - {Gb(high)}, most likely ~{Gb(mid)} (an ESTIMATE from measured encodes; the real result is measured and shown afterwards)." +
            (refused > 0 ? $" {refused:N0} file(s) are already lean: they would probably be refused by the keep-if-smaller rule (the original stays, the time is spent)." : ""));
        sb.AppendLine();
        var enc = s.Mode == EncoderMode.Gpu ? $"GPU (NVENC HEVC, CQ {s.GpuCq})" : $"CPU (x265 {s.X265Preset}, CRF {s.CpuCrf})";
        var jobSpecs = jobs.Where(j => j.Plan.WritesFile && (j.Entry.Probe.DurationSec ?? 0) > 0).Select(j => (j.Plan, j.Entry.Probe.DurationSec ?? 0)).ToList();
        if (jobSpecs.Count > 0)
        {
            var (x, measured) = SpeedEstimator.EncodeSpeed(s);
            var hours = SpeedEstimator.EstimateHours(jobSpecs, s);
            sb.AppendLine($"Estimated time: about {SpeedEstimator.Format(hours)} ({(measured ? $"from your benchmark, {x:0.0}x real time" : $"typical speed for this mode, about {x:0.0}x real time; press Run 20-second benchmark in Setup for a measured estimate")}).");
        }
        sb.AppendLine($"Encoder: {enc}.  Container: {(s.Container == OutputContainer.Mp4 ? "MP4 (faststart)" : "MKV")} (MKV automatically when kept image subtitles need it).");
        sb.AppendLine($"Subtitles: {(s.KeepEmbeddedSubsIfNoExternal ? "embedded subtitles kept when there is no external file" : "embedded subtitles removed")}.  Metadata cleanup: {(s.CleanJunkMetadata ? "on" : "off")}.");
        sb.AppendLine($"Timestamps: {(s.PreserveTimestamps ? "created and modified dates are copied to the new file" : "not preserved")}.");
        sb.AppendLine();
        var holding = HoldingArea.Root(s.MediaFolder, s.OriginalsFolderName);
        sb.AppendLine(s.Originals switch
        {
            OriginalsHandling.MoveToHolding => "Originals are MOVED to:\r\n    " + holding + "\r\n(same folder structure; outside the Media folder; nothing is deleted).",
            OriginalsHandling.RenameInPlace => "Originals are renamed to \"<name>.convertr-orig\" in the same folder; nothing is deleted.",
            _ => "Originals are DELETED after each result is verified (your choice in Setup). They cannot be restored.",
        });
        sb.AppendLine();
        sb.AppendLine("Each file is converted to a temporary \".convertr-part\" file next to the original, checked (streams, length, size, a test decode), and only then swapped in. If anything is wrong the original stays untouched and the temporary file is deleted. A run can be stopped at any time and resumed later, and Undo restores the originals.");
        var skipped = q.Jobs.Count(j => j.State == JobState.Skipped);
        if (skipped > 0) sb.AppendLine().AppendLine($"{skipped:N0} file(s) will be skipped because the new name already exists or another file in this run uses it.");
        if (s.BatchLimitGb > 0) sb.AppendLine().AppendLine($"Batch limit: the run stops starting new files after {s.BatchLimitGb:N0} GB.");
        if (s.QuietHoursEnabled) sb.AppendLine().AppendLine($"Run window: files are only started between {s.QuietHoursStart} and {s.QuietHoursEnd}.");
        sb.AppendLine().AppendLine("Log: " + AppPaths.LogsDir);
        var first = !_settings.FirstRunConfirmed;
        if (first) sb.AppendLine().AppendLine("This is the first conversion. Tip: filter the table to a few files first to see the result, then do the rest.");
        return ($"Convert {jobs.Count:N0} file(s) ({Gb(bytes)}) in {s.MediaFolder}", sb.ToString().TrimEnd());
    }

    /// <summary>An unfinished queue from an earlier run: offer to resume it. Returns true when the user chose to resume (so the caller does nothing more).</summary>
    private async Task<bool> OfferExistingQueueAsync()
    {
        var old = QueueStore.Load(AppPaths.QueuePath);
        if (old == null || old.Remaining == 0 || old.DryRun) return false;
        var ans = MessageBox.Show(this, $"An earlier run (started {old.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}) was not finished: {old.Remaining:N0} file(s) are still queued.\r\n\r\nYes = resume that run now\r\nNo = discard it and start a new one\r\nCancel = do nothing",
            "Convertr", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (ans == DialogResult.Cancel) return true;
        if (ans == DialogResult.No) { QueueStore.Delete(AppPaths.QueuePath); return false; }
        var loc = FfmpegLocator.Find(_settings.FfmpegFolder);
        if (loc == null || loc.FfmpegPath.Length == 0) { MessageBox.Show(this, "ffmpeg.exe was not found. Open Setup first.", "Convertr"); return true; }
        await RunQueueAsync(old, loc);
        return true;
    }

    private async Task OfferResumeAsync()
    {
        try
        {
            var old = QueueStore.Load(AppPaths.QueuePath);
            if (old == null || old.Remaining == 0 || old.DryRun) return;
            await OfferExistingQueueAsync();
        }
        catch (Exception ex) { _lblProgress.Text = "Could not check for an unfinished run: " + ex.Message; }
    }

    private async Task RunQueueAsync(QueueFile q, FfmpegLocation loc)
    {
        _queue = q;
        _rowByPath.Clear();
        foreach (var r in _rows) _rowByPath[r.Entry.FullPath] = r;
        foreach (var j in q.Jobs)
            if (_rowByPathTry(j.Entry.FullPath) is { } row) { row.JobState = j.State; row.Status = j.State == JobState.Pending ? "queued" : j.Message; }
        _grid.Invalidate();

        var settings = SettingsStore.Clone(_settings);
        settings.DryRun = q.DryRun || settings.DryRun;
        q.DryRun = settings.DryRun;
        Directory.CreateDirectory(AppPaths.LogsDir);
        var now = DateTime.Now;
        var log = new RunLog(RunLog.NewPath(AppPaths.LogsDir, now));
        _lastLogPath = log.Path ?? "";
        var journal = new UndoJournal(UndoJournal.NewPath(AppPaths.LogsDir, now));
        var eng = new ConversionEngine(settings, new FfmpegProcessRunner(loc.FfmpegPath, settings.ProcessPriority), new FfprobeRunner(loc.FfprobePath), log, journal, settings.DryRun ? null : AppPaths.QueuePath);
        _engine = eng;
        eng.JobChanged += j => BeginInvoke(new Action(() => OnJobChanged(j)));
        eng.ProgressChanged += p => BeginInvoke(new Action(() => OnEngineProgress(q, p)));
        eng.StatusChanged += t => BeginInvoke(new Action(() => _lblProgress.Text = t));

        _running = true;
        UpdateButtons();
        _progress.Style = ProgressBarStyle.Continuous; _progress.Maximum = 1000; _progress.Value = 0;
        _lblProgress.Text = settings.DryRun ? "Dry run: writing the ffmpeg commands to the log..." : "Starting...";
        RunSummary sum;
        try { sum = await Task.Run(() => eng.RunAsync(q)); }
        catch (Exception ex)
        {
            _running = false; _engine = null; UpdateButtons();
            MessageBox.Show(this, "The conversion stopped unexpectedly:\r\n" + ex.Message + "\r\n\r\nThe originals are untouched or were put back. Details are in the log:\r\n" + _lastLogPath, "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _running = false;
        _engine = null;
        if (!settings.DryRun && sum.Pending == 0) QueueStore.Delete(AppPaths.QueuePath);
        foreach (var j in q.Jobs) ApplyJobToRow(j);
        UpdateButtons();
        _progress.Value = sum.Pending == 0 && !sum.Stopped ? _progress.Maximum : _progress.Value;
        if (settings.DryRun)
        {
            _lblProgress.Text = $"Dry run finished: {q.Jobs.Count(j => !j.IsFinal):N0} ffmpeg command(s) were written to the log; nothing was changed.   Log: {_lastLogPath}";
        }
        else
        {
            var t = sum.Elapsed;
            var took = t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0}s";
            var pct = sum.OriginalBytes > 0 ? 100.0 * sum.SavedBytes / sum.OriginalBytes : 0;
            _lblProgress.Text = (sum.Stopped ? "Run stopped" + (sum.StopReason.Length > 0 ? " (" + sum.StopReason + ")" : "") + $": {sum.Pending:N0} file(s) left in the queue; start Convert again to resume.   " : "Run finished.   ") +
                                $"{sum.Done:N0} converted, {sum.NotWorthIt:N0} kept as they were (result not smaller enough), {sum.Failed:N0} failed, {sum.Skipped:N0} skipped, in {took}.   Log: {_lastLogPath}";
            _lblSavings.Text = $"Measured result: {Gb(sum.OriginalBytes)} became {Gb(sum.NewBytes)}, saved {Gb(sum.SavedBytes)} ({pct:0.0}%) over {sum.Done:N0} converted file(s).   Scan again to refresh the table.";
        }
        if (_closeRequested) Close();
    }

    private void OnJobChanged(QueueJob j)
    {
        ApplyJobToRow(j);
        _grid.Invalidate();
    }

    private void ApplyJobToRow(QueueJob j)
    {
        if (_rowByPathTry(j.Entry.FullPath) is not { } row) return;
        row.JobState = j.State;
        row.Status = j.State switch
        {
            JobState.Pending => j.Message.Length > 0 ? j.Message : "queued",
            JobState.Encoding => "encoding...",
            JobState.Verifying => "verifying...",
            JobState.Swapping => "swapping in...",
            JobState.Done => $"done, saved {j.SavingPercent:0}%",
            JobState.NotWorthIt => $"kept original (saved only {j.SavingPercent:0}%)",
            _ => j.Message,
        };
    }

    private void OnEngineProgress(QueueFile q, EngineProgress p)
    {
        if (!_running) return;
        var overall = p.TotalBytes > 0 ? (double)p.ProcessedBytes / p.TotalBytes : 0;
        if (_progress.Maximum != 1000) { _progress.Style = ProgressBarStyle.Continuous; _progress.Maximum = 1000; }
        _progress.Value = Math.Clamp((int)(overall * 1000), 0, 1000);
        var sb = new StringBuilder();
        if (p.Job != null)
        {
            if (_rowByPathTry(p.Job.Entry.FullPath) is { } row && p.Job.State == JobState.Encoding)
                row.Status = $"{p.JobFraction:P0}" + (p.Speed is double sp ? $"  {sp:0.0}x" : "");
            sb.Append($"Converting {Math.Min(p.FinishedJobs + 1, p.TotalJobs):N0} of {p.TotalJobs:N0}: {p.Job.Entry.FileName}   {p.JobFraction:P0}");
            if (p.Speed is double s2) sb.Append($"   speed {s2:0.0}x");
            if (p.JobEta is TimeSpan e1) sb.Append($"   file ETA {Fmt(e1)}");
            sb.Append("   |   ");
        }
        sb.Append($"Overall {overall:P0}  ({Gb(p.ProcessedBytes)} of {Gb(p.TotalBytes)})");
        if (p.OverallEta is TimeSpan e2) sb.Append($"   ETA {Fmt(e2)}");
        var saved = q.Jobs.Where(j => j.State == JobState.Done).Sum(j => j.Entry.SizeBytes - j.OutputBytes);
        if (saved != 0) sb.Append($"   |   saved so far {Gb(saved)}");
        _lblProgress.Text = sb.ToString();
        _grid.Invalidate();
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void TogglePause()
    {
        if (_engine == null) return;
        if (_engine.Paused) { _engine.Resume(); _btnPause.Text = "Pause"; _lblProgress.Text = "Resumed."; }
        else { _engine.Pause(); _btnPause.Text = "Resume"; _lblProgress.Text = "Paused: ffmpeg is suspended; press Resume to continue where it stopped."; }
    }

    private void StopNow()
    {
        if (_engine == null) return;
        var ans = MessageBox.Show(this, "Stop now?\r\n\r\nThe file being converted is abandoned: its partial output is deleted and the original is untouched. The rest stays queued, so you can resume later.\r\n\r\n(\"Stop after this file\" finishes the current file first.)",
            "Convertr", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (ans != DialogResult.Yes) return;
        _engine.Stop();
        _lblProgress.Text = "Stopping...";
    }

    /// <summary>Screenshot/test hook: shows the window as it looks in the middle of a run. No engine is started.</summary>
    /// <summary>Test/screenshot hook: pretend the run is over so the window can close without the prompt.</summary>
    public void EndRunState() { _running = false; }

    public void ShowRunState(QueueFile q, EngineProgress p, bool paused = false)
    {
        _queue = q;
        _rowByPath.Clear();
        foreach (var r in _rows) _rowByPath[r.Entry.FullPath] = r;
        foreach (var j in q.Jobs) ApplyJobToRow(j);
        _running = true;
        UpdateButtons();
        if (paused) _btnPause.Text = "Resume";
        OnEngineProgress(q, p);
        _grid.Invalidate();
    }

    /// <summary>Screenshot hook: the text of the summary dialog for the given rows.</summary>
    public (string Heading, string Body) PreviewRunSummary(IEnumerable<PlanRow> rows)
    {
        var q = QueueBuilder.Build(rows.Where(r => r.Plan.WritesFile).Select(r => (r.Entry, r.Plan)), _settings, _settings.MediaFolder, "preview");
        return BuildRunSummary(q, q.Jobs.Where(j => !j.IsFinal).ToList());
    }

    public IReadOnlyList<PlanRow> Rows => _rows;

    // ================= undo =================
    private void OpenUndo()
    {
        List<UndoItem> items;
        try { items = Directory.Exists(AppPaths.LogsDir) ? UndoService.Load(Directory.GetFiles(AppPaths.LogsDir, "undo_*.csv")) : new(); }
        catch (Exception ex) { MessageBox.Show(this, "Could not read the undo journals:\r\n" + ex.Message, "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (items.Count == 0) { MessageBox.Show(this, "There is nothing to undo yet.", "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var f = new UndoForm(items);
        if (f.ShowDialog(this) != DialogResult.OK || f.Chosen.Count == 0) return;
        Directory.CreateDirectory(AppPaths.LogsDir);
        var now = DateTime.Now;
        var log = new RunLog(RunLog.NewPath(AppPaths.LogsDir, now));
        var res = UndoService.Execute(f.Chosen, new UndoJournal(UndoJournal.NewPath(AppPaths.LogsDir, now.AddSeconds(1))), log);
        var ok = res.Count(r => r.Success);
        var bad = res.Where(r => !r.Success).ToList();
        _lblProgress.Text = $"Undo: {ok:N0} original(s) restored" + (bad.Count > 0 ? $", {bad.Count:N0} could not be restored ({bad[0].Message})" : "") + ". Converted files were moved to the \"" + HoldingArea.UndoneFolderName + "\" folder.   Scan again to refresh the table.   Log: " + log.Path;
        UpdateButtons();
    }

    // ================= export =================
    private void ExportCsv()
    {
        if (_rows.Count == 0) return;
        using var d = new SaveFileDialog
        {
            Title = "Export plan",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"plan_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            InitialDirectory = AppContext.BaseDirectory,
            OverwritePrompt = true,
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var full = System.IO.Path.GetFullPath(d.FileName);
        if (_settings.MediaFolder.Length > 0)
        {
            var root = System.IO.Path.GetFullPath(_settings.MediaFolder).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Convertr never writes into your Media folder. Choose a location outside it.", "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        try
        {
            PlanCsv.Write(full, _rows.Select(r => (r.Entry, r.Plan)));
            _lblProgress.Text = "Plan exported: " + full;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not write the file:\r\n" + ex.Message, "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); _searchTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
