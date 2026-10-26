// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr.Core;

namespace Convertr;

/// <summary>
/// Setup window: every option from DESIGN.md section 9 is changeable here. Edits a copy of the settings; Save applies it, Cancel discards it.
/// </summary>
public sealed class SetupForm : Form
{
    private readonly Settings _s;
    private readonly ToolTip _tip = new() { AutoPopDelay = 12000, InitialDelay = 400 };
    private List<string> _junkPatterns;
    private readonly ThemeMode _originalTheme;

    public Settings Result => _s;

    // encoder
    private readonly RadioButton _rbGpu = new() { Text = "GPU (NVENC, fast)", AutoSize = true };
    private readonly RadioButton _rbCpu = new() { Text = "CPU (x265, smaller)", AutoSize = true };
    private readonly ComboBox _preset = Combo("ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow");
    private readonly NumericUpDown _cpuCrf = Num(10, 40);
    private readonly NumericUpDown _gpuCq = Num(10, 40);
    private readonly NumericUpDown _legacyCrf = Num(10, 40);
    private readonly ComboBox _bitDepth = Combo("8-bit (HEVC Main)", "10-bit (HEVC Main10)");
    private readonly NumericUpDown _parGpu = Num(1, 4);
    private readonly NumericUpDown _parCpu = Num(1, 4);
    private readonly ComboBox _priority = Combo("BelowNormal", "Normal", "Idle");
    // rules
    private readonly NumericUpDown _k1080 = Num(100, 100000, 100);
    private readonly NumericUpDown _k720 = Num(100, 100000, 100);
    private readonly NumericUpDown _kSd = Num(100, 100000, 100);
    private readonly NumericUpDown _kLow = Num(100, 100000, 100);
    private readonly NumericUpDown _keepPct = Num(0, 90);
    private readonly NumericUpDown _keepMb = Num(0, 100000, 10);
    private readonly ComboBox _container = Combo("MP4 (+faststart)", "MKV");
    private readonly CheckBox _plexVersions = new() { Text = "Scan Plex \"Plex Versions\" (optimized version) folders too", AutoSize = true };
    private readonly CheckBox _allowHdr = new() { Text = "Process HDR / Dolby Vision / 4K files (otherwise they are marked Review)", AutoSize = true };
    private readonly CheckBox _procInterlaced = new() { Text = "Process interlaced files (deinterlace)", AutoSize = true };
    // appearance
    private readonly ComboBox _theme = Combo("System (follow Windows)", "Light", "Dark");
    // audio
    private readonly ComboBox _audioTarget = Combo("AC3 640k (5.1)", "AAC 384k");
    private readonly CheckBox _keepOrigAudio = new() { Text = "Keep the original audio as a second track", AutoSize = true };
    private readonly CheckBox _addAac = new() { Text = "Add an AAC 2.0 compatibility track (for browsers)", AutoSize = true };
    private readonly CheckBox _keepLang = new() { Text = "Keep only these audio languages", AutoSize = true };
    private readonly TextBox _langs = new() { Width = 120 };
    // subtitles
    private readonly CheckBox _keepSubs = new() { Text = "Keep embedded subtitles when no external subtitle file exists", AutoSize = true };
    private readonly CheckBox _stripSkipped = new() { Text = "Strip subtitles on skipped files too (remux, no re-encode)", AutoSize = true };
    // metadata
    private readonly CheckBox _clean = new() { Text = "Clean up junk metadata", AutoSize = true };
    private readonly CheckBox _cleanSkipped = new() { Text = "Clean metadata on skipped files too (remux only)", AutoSize = true };
    private readonly CheckBox _dropChapters = new() { Text = "Drop chapters", AutoSize = true };
    private readonly CheckBox _keepTitle = new() { Text = "Keep an embedded title when it matches the file name", AutoSize = true };
    private readonly CheckBox _keepAudioTitles = new() { Text = "Keep audio track titles such as \"Commentary\"", AutoSize = true };
    private readonly Button _patterns = new() { Text = "Edit junk patterns...", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    // output
    private readonly CheckBox _timestamps = new() { Text = "Preserve file timestamps", AutoSize = true };
    private readonly RadioButton _orMove = new() { Text = "Move to a holding folder", AutoSize = true };
    private readonly RadioButton _orRename = new() { Text = "Rename in place (.convertr-orig)", AutoSize = true };
    private readonly RadioButton _orDelete = new() { Text = "Delete after verification (asks every run)", AutoSize = true };
    private readonly TextBox _holding = new() { Width = 200 };
    private readonly NumericUpDown _purgeDays = Num(0, 3650);
    private readonly NumericUpDown _batchGb = Num(0, 100000, 10);
    private readonly CheckBox _verifyDecode = new() { Text = "Test-decode each result before it replaces the original", AutoSize = true };
    // run
    private readonly CheckBox _quiet = new() { Text = "Only run between", AutoSize = true };
    private readonly DateTimePicker _qStart = TimePicker();
    private readonly DateTimePicker _qEnd = TimePicker();
    // ffmpeg
    private readonly TextBox _ffPath = new() { Width = 250 };
    private readonly Button _ffBrowse = new() { Text = "Browse...", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly Button _ffDetect = new() { Text = "Detect", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly Button _ffDownload = new() { Text = "Download FFmpeg", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly Button _ffDownloadOld = new() { Text = "Download older", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly ProgressBar _ffProgress = new() { Width = 440, Height = 16, Minimum = 0, Maximum = 1000, Visible = false };
    private readonly Label _ffProgressText = new() { AutoSize = true, MaximumSize = new Size(440, 0), Tag = "muted", Visible = false };
    private CancellationTokenSource? _ffCts;
    private readonly Label _ffStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly NumericUpDown _probeThreads = Num(1, 16);
    // hardware
    private readonly ComboBox _policy = Combo("Auto (recommended)", "Prefer CPU quality", "Prefer GPU speed");
    private readonly Label _hwText = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly Button _hwDetect = new() { Text = "Re-detect hardware", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly Button _hwReset = new() { Text = "Reset to recommended", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly Button _hwBench = new() { Text = "Run 20-second benchmark", AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
    private readonly Label _hwBenchText = new() { AutoSize = true, MaximumSize = new Size(440, 0), Tag = "muted" };
    private HardwareProfile? _hw;
    private readonly HashSet<string> _userModified;
    private Settings _baseline;
    private int _x265Threads;
    private double? _benchCpu, _benchGpu;
    private DateTime? _benchUtc;
    private CancellationTokenSource? _hwCts;
    private bool _loading = true;

    /// <summary>Files that would be converted now (for the benchmark sample and the queue estimate). Set by the main window.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<ScanEntry> QueueEntries { get; set; } = Array.Empty<ScanEntry>();

    public SetupForm(Settings settings)
    {
        _s = SettingsStore.Clone(settings);
        _baseline = SettingsStore.Clone(settings);
        _userModified = new HashSet<string>(_s.UserModified);
        _hw = _s.Hardware;
        _x265Threads = _s.X265Threads;
        _benchCpu = _s.BenchmarkCpuX; _benchGpu = _s.BenchmarkGpuX; _benchUtc = _s.BenchmarkUtc;
        _originalTheme = Theming.Mode;
        _junkPatterns = new List<string>(_s.JunkPatterns);
        Text = "Convertr Setup";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;   // resizable: the content stretches with the width and scrolls when the window is small
        MaximizeBox = true;
        MinimizeBox = false;
        SizeGripStyle = SizeGripStyle.Show;
        MinimumSize = new Size(560, 420);
        Icon = AppIcon.Load();
        AutoScaleMode = AutoScaleMode.Font;

        var left = Column();
        var right = Column();

        left.Controls.Add(G("Encoder", t =>
        {
            Row(t, "Mode", Flow(_rbGpu, _rbCpu));
            Row(t, "x265 preset", _preset);
            Row(t, "CPU quality (x265 CRF)", _cpuCrf);
            Row(t, "GPU quality (NVENC CQ)", _gpuCq);
            Row(t, "Legacy files (x264 CRF)", _legacyCrf);
            Row(t, "HEVC bit depth", _bitDepth);
            Row(t, "Parallel jobs: GPU / CPU", Flow(_parGpu, _parCpu));
            Row(t, "Process priority", _priority);
        }));
        left.Controls.Add(G("Rules (bitrate thresholds change the preview now)", t =>
        {
            Row(t, "Re-encode H.264 at or above (kbps):", null);
            Row(t, "    1080p", _k1080);
            Row(t, "    720p", _k720);
            Row(t, "    576p / 480p", _kSd);
            Row(t, "    below 480p", _kLow);
            Row(t, "Keep result only if saving >= (%)", _keepPct);
            Row(t, "... and at least (MB)", _keepMb);
            Row(t, "Output container", _container);
            Span(t, _allowHdr);
            Span(t, _procInterlaced);
            Span(t, _plexVersions);
        }));
        left.Controls.Add(G("Audio", t =>
        {
            Row(t, "DTS and other formats become", _audioTarget);
            Span(t, _keepOrigAudio);
            Span(t, _addAac);
            Row(t, null, Flow(_keepLang, _langs));
        }));

        left.Controls.Add(G("Appearance", t =>
        {
            Row(t, "Theme", _theme);
        }));

        right.Controls.Add(G("Subtitles (embedded subtitles are stripped by default)", t =>
        {
            Span(t, _keepSubs);
            Span(t, _stripSkipped);
            Span(t, Hint("External .srt / .sub / .idx / .ass files are never touched."));
        }));
        right.Controls.Add(G("Metadata", t =>
        {
            Span(t, _clean);
            Span(t, _cleanSkipped);
            Span(t, _keepTitle);
            Span(t, _dropChapters);
            Span(t, _keepAudioTitles);
            Span(t, _patterns);
        }));
        right.Controls.Add(G("Output and originals", t =>
        {
            Span(t, _timestamps);
            Row(t, "Originals", Flow(_orMove));
            Row(t, null, Flow(_orRename));
            Row(t, null, Flow(_orDelete));
            Row(t, "Holding folder name", _holding);
            Row(t, "Purge originals after (days)", _purgeDays);
            Row(t, "Batch size limit (GB, 0 = none)", _batchGb);
            Span(t, _verifyDecode);
        }));
        left.Controls.Add(G("Run schedule", t =>
        {
            Row(t, null, Flow(_quiet, _qStart, new Label { Text = "and", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _qEnd));
        }));
        right.Controls.Add(G("Hardware (detected defaults)", t =>
        {
            Span(t, _hwText);
            Row(t, "Defaults policy", _policy);
            Row(t, null, Flow(_hwDetect, _hwReset));
            Row(t, null, _hwBench);
            Span(t, _hwBenchText);
        }));
        right.Controls.Add(G("FFmpeg", t =>
        {
            Row(t, "ffmpeg / ffprobe folder", _ffPath);
            Row(t, null, Flow(_ffBrowse, _ffDetect));
            Row(t, null, Flow(_ffDownload, _ffDownloadOld));
            Span(t, _ffProgress);
            Span(t, _ffProgressText);
            Span(t, _ffStatus);
            Row(t, "Parallel probes", _probeThreads);
        }));

        var cols = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cols.Controls.Add(left, 0, 0);
        cols.Controls.Add(right, 1, 0);

        var note = new Label
        {
            Text = "Settings file: " + SettingsStore.DefaultPath,
            AutoSize = false, Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(10, 4, 10, 0), Tag = "muted",
        };
        var save = new Button { Text = "Save", AutoSize = true, Padding = new Padding(14, 3, 14, 3), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(14, 3, 14, 3), DialogResult = DialogResult.Cancel };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(10, 6, 10, 6) };
        bar.Controls.Add(cancel);
        bar.Controls.Add(save);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, AutoScrollMinSize = new Size(MinContentWidth, 0) };
        scroll.Controls.Add(cols);
        Controls.Add(scroll);
        Controls.Add(note);
        Controls.Add(bar);
        AcceptButton = save;
        CancelButton = cancel;
        ActiveControl = save;

        // tooltips for the less obvious options
        _tip.SetToolTip(_keepSubs, "Off (default): all embedded subtitles are stripped when a file is converted. On: they are kept for files that have no external subtitle file next to the video.");
        _tip.SetToolTip(_stripSkipped, "Off (default): files that are skipped are never touched, so their embedded subtitles stay. On: such files are remuxed to drop the subtitles.");
        _tip.SetToolTip(_cleanSkipped, "Off (default): skipped files keep their metadata. On: skipped files with junk metadata become a quick remux (no re-encode).");
        _tip.SetToolTip(_keepPct, "After a size re-encode the output is kept only if it is at least this much smaller...");
        _tip.SetToolTip(_keepMb, "...and at least this many MB smaller.");
        _tip.SetToolTip(_plexVersions, "Off (default): the folders Plex creates for optimized versions are not scanned, because Plex manages those copies. Takes effect on the next scan.");
        _tip.SetToolTip(_allowHdr, "Off (default): HDR, Dolby Vision and 4K files are only marked Review.");
        _tip.SetToolTip(_procInterlaced, "On (default): interlaced files are planned like any other and deinterlaced (bwdif) when conversion is enabled. Off: they are only marked Review.");
        _tip.SetToolTip(_k1080, "Files with video bitrate at or above this value are re-encoded to HEVC. Bitrates shown with * in the table are estimates.");

        _patterns.Click += (_, _) =>
        {
            using var f = new TextEditForm("Junk metadata patterns",
                "One regular expression per line (case-insensitive). A tag, stream title or chapter title that matches is treated as junk and removed when metadata is cleaned.", _junkPatterns);
            if (f.ShowDialog(this) == DialogResult.OK) _junkPatterns = f.Lines;
        };
        _ffBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Folder that contains ffmpeg.exe and ffprobe.exe", UseDescriptionForTitle = true, SelectedPath = _ffPath.Text };
            if (d.ShowDialog(this) == DialogResult.OK) { _ffPath.Text = d.SelectedPath; _ = DetectAsync(); }
        };
        _ffDetect.Click += async (_, _) => await DetectAsync();
        _hwDetect.Click += async (_, _) => await RedetectHardwareAsync();
        _hwReset.Click += (_, _) => ApplyHardwareDecision(reset: true);
        _hwBench.Click += async (_, _) => await RunBenchmarkAsync();
        _policy.SelectedIndexChanged += (_, _) => { if (!_loading) ApplyHardwareDecision(reset: false); };
        _tip.SetToolTip(_policy, "Auto: x265 on the CPU for the smallest files (the GPU only when the CPU is weak). Prefer CPU quality: always the CPU. Prefer GPU speed: NVENC whenever it works (fast, but files stay larger).");
        _tip.SetToolTip(_hwReset, "Forget the settings you changed by hand and apply the recommended defaults for this computer.");
        _tip.SetToolTip(_hwBench, "Encodes 20 seconds of one of the files in the list with the chosen mode into your temp folder (deleted afterwards), then estimates the time for the whole queue. Scan first.");
        _ffDownload.Click += async (_, _) => await DownloadFfmpegAsync(false);
        _ffDownloadOld.Click += async (_, _) => await DownloadOlderAsync();
        FormClosing += (_, _) => { _ffCts?.Cancel(); _hwCts?.Cancel(); };
        _orMove.CheckedChanged += (_, _) => _holding.Enabled = _orMove.Checked;
        _quiet.CheckedChanged += (_, _) => { _qStart.Enabled = _qEnd.Enabled = _quiet.Checked; };
        _keepLang.CheckedChanged += (_, _) => _langs.Enabled = _keepLang.Checked;

        LoadFrom(_s);
        _loading = false;
        ShowHardware();
        // the theme changes everywhere as soon as it is picked; Cancel puts the old one back
        _theme.SelectedIndexChanged += (_, _) => Theming.SetMode(ThemeFromIndex(_theme.SelectedIndex));
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK) SaveTo(_s);
            else Theming.SetMode(_originalTheme);
        };
        GridHelper.ClearTextSelection(this);
        cols.SizeChanged += (_, _) => FitWrappedLabels(cols);
        Load += (_, _) =>
        {
            // default size = the natural size of the content; a remembered size wins. Both are kept within the screen (the panel scrolls).
            var wa = Screen.FromControl(this).WorkingArea;
            var contentW = DefaultContentWidth;
            var natural = new Size(contentW + SystemInformation.VerticalScrollBarWidth + 24, cols.GetPreferredSize(new Size(contentW, 0)).Height + note.Height + bar.Height + 48);
            ClientSize = new Size(Math.Min(natural.Width, wa.Width - 40), Math.Min(natural.Height, wa.Height - 80));
            var size = DialogGeometry.ResolveSize(Size, _s.SetupWidth, _s.SetupHeight, MinimumSize, wa);
            if (size != Size) Size = size;
            FitWrappedLabels(cols);
        };
        Shown += async (_, _) => await DetectAsync();
        Theming.Track(_tip);
        Theming.Register(this);
        DialogPlacement.Attach(this);
    }

    // ---------- data <-> controls ----------
    private void LoadFrom(Settings s)
    {
        _rbGpu.Checked = s.Mode == EncoderMode.Gpu;
        _rbCpu.Checked = s.Mode == EncoderMode.Cpu;
        _preset.Text = s.X265Preset;
        _cpuCrf.Value = s.CpuCrf; _gpuCq.Value = s.GpuCq; _legacyCrf.Value = s.LegacyCrf;
        _bitDepth.SelectedIndex = s.UseHevc10Bit ? 1 : 0;
        _parGpu.Value = s.ParallelGpuJobs; _parCpu.Value = s.ParallelCpuJobs;
        _priority.Text = s.ProcessPriority;
        _k1080.Value = s.Kbps1080; _k720.Value = s.Kbps720; _kSd.Value = s.KbpsSd; _kLow.Value = s.KbpsBelowSd;
        _keepPct.Value = s.KeepIfSmallerPercent; _keepMb.Value = s.KeepIfSmallerMb;
        _container.SelectedIndex = s.Container == OutputContainer.Mp4 ? 0 : 1;
        _allowHdr.Checked = s.AllowHdr4k;
        _procInterlaced.Checked = s.ProcessInterlaced;
        _plexVersions.Checked = s.IncludePlexVersions;
        _theme.SelectedIndex = s.Theme switch { ThemeMode.Light => 1, ThemeMode.Dark => 2, _ => 0 };
        _audioTarget.SelectedIndex = s.DtsOtherTarget == AudioFallback.Ac3 ? 0 : 1;
        _keepOrigAudio.Checked = s.KeepOriginalAudioAsSecondTrack; _addAac.Checked = s.AddAacCompatTrack;
        _keepLang.Checked = s.KeepOnlyLanguages; _langs.Text = s.KeepLanguages; _langs.Enabled = s.KeepOnlyLanguages;
        _keepSubs.Checked = s.KeepEmbeddedSubsIfNoExternal; _stripSkipped.Checked = s.StripSubsOnSkipped;
        _clean.Checked = s.CleanJunkMetadata; _cleanSkipped.Checked = s.CleanMetadataOnSkipped;
        _dropChapters.Checked = s.DropChapters; _keepTitle.Checked = s.KeepSensibleTitle; _keepAudioTitles.Checked = s.KeepAudioTrackTitles;
        _timestamps.Checked = s.PreserveTimestamps;
        _orMove.Checked = s.Originals == OriginalsHandling.MoveToHolding;
        _orRename.Checked = s.Originals == OriginalsHandling.RenameInPlace;
        _orDelete.Checked = s.Originals == OriginalsHandling.DeleteAfterVerify;
        _holding.Text = s.OriginalsFolderName; _holding.Enabled = _orMove.Checked;
        _purgeDays.Value = s.PurgeAfterDays; _batchGb.Value = s.BatchLimitGb; _verifyDecode.Checked = s.VerifyDecode;
        _quiet.Checked = s.QuietHoursEnabled;
        _qStart.Value = ParseTime(s.QuietHoursStart, 1, 0); _qEnd.Value = ParseTime(s.QuietHoursEnd, 15, 0);
        _qStart.Enabled = _qEnd.Enabled = s.QuietHoursEnabled;
        _ffPath.Text = s.FfmpegFolder; _probeThreads.Value = s.ProbeThreads;
        _policy.SelectedIndex = (int)s.DefaultsPolicy;
    }

    private void SaveTo(Settings s)
    {
        s.Mode = _rbCpu.Checked ? EncoderMode.Cpu : EncoderMode.Gpu;
        s.X265Preset = _preset.Text;
        s.CpuCrf = (int)_cpuCrf.Value; s.GpuCq = (int)_gpuCq.Value; s.LegacyCrf = (int)_legacyCrf.Value;
        s.UseHevc10Bit = _bitDepth.SelectedIndex == 1;
        s.ParallelGpuJobs = (int)_parGpu.Value; s.ParallelCpuJobs = (int)_parCpu.Value;
        s.ProcessPriority = _priority.Text;
        s.Kbps1080 = (int)_k1080.Value; s.Kbps720 = (int)_k720.Value; s.KbpsSd = (int)_kSd.Value; s.KbpsBelowSd = (int)_kLow.Value;
        s.KeepIfSmallerPercent = (int)_keepPct.Value; s.KeepIfSmallerMb = (int)_keepMb.Value;
        s.Container = _container.SelectedIndex == 0 ? OutputContainer.Mp4 : OutputContainer.Mkv;
        s.AllowHdr4k = _allowHdr.Checked;
        s.ProcessInterlaced = _procInterlaced.Checked;
        s.IncludePlexVersions = _plexVersions.Checked;
        s.Theme = ThemeFromIndex(_theme.SelectedIndex);
        s.DtsOtherTarget = _audioTarget.SelectedIndex == 0 ? AudioFallback.Ac3 : AudioFallback.Aac;
        s.KeepOriginalAudioAsSecondTrack = _keepOrigAudio.Checked; s.AddAacCompatTrack = _addAac.Checked;
        s.KeepOnlyLanguages = _keepLang.Checked; s.KeepLanguages = _langs.Text.Trim();
        s.KeepEmbeddedSubsIfNoExternal = _keepSubs.Checked; s.StripSubsOnSkipped = _stripSkipped.Checked;
        s.CleanJunkMetadata = _clean.Checked; s.CleanMetadataOnSkipped = _cleanSkipped.Checked;
        s.DropChapters = _dropChapters.Checked; s.KeepSensibleTitle = _keepTitle.Checked; s.KeepAudioTrackTitles = _keepAudioTitles.Checked;
        s.JunkPatterns = new List<string>(_junkPatterns);
        s.PreserveTimestamps = _timestamps.Checked;
        s.Originals = _orDelete.Checked ? OriginalsHandling.DeleteAfterVerify : _orRename.Checked ? OriginalsHandling.RenameInPlace : OriginalsHandling.MoveToHolding;
        s.OriginalsFolderName = string.IsNullOrWhiteSpace(_holding.Text) ? "_Convertr_Originals" : _holding.Text.Trim();
        s.PurgeAfterDays = (int)_purgeDays.Value; s.BatchLimitGb = (int)_batchGb.Value; s.VerifyDecode = _verifyDecode.Checked;
        s.QuietHoursEnabled = _quiet.Checked;
        s.QuietHoursStart = _qStart.Value.ToString("HH:mm"); s.QuietHoursEnd = _qEnd.Value.ToString("HH:mm");
        s.FfmpegFolder = _ffPath.Text.Trim(); s.ProbeThreads = (int)_probeThreads.Value;
        s.DefaultsPolicy = (DefaultsPolicy)Math.Max(0, _policy.SelectedIndex);
        s.X265Threads = _x265Threads; s.Hardware = _hw;
        s.BenchmarkCpuX = _benchCpu; s.BenchmarkGpuX = _benchGpu; s.BenchmarkUtc = _benchUtc;
        if (IsHandleCreated)
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;   // a maximized window is not remembered, only its normal size
            s.SetupWidth = b.Width; s.SetupHeight = b.Height;
        }
        s.UserModified = new List<string>(_userModified);
        TunedSettings.MarkUserChanges(_baseline, s);   // anything the user changed in this window is theirs, detection never overwrites it
        s.Normalize();
    }

    private async Task DetectAsync()
    {
        if (IsDisposed) return;
        _ffStatus.Tag = null; Theming.Apply(_ffStatus);
        _ffStatus.Text = "Looking for ffmpeg...";
        var loc = FfmpegLocator.Find(_ffPath.Text.Trim());
        if (loc == null)
        {
            _ffStatus.Text = "FFmpeg not found. Press \"Download FFmpeg\" to get it, or Browse to the folder that contains ffmpeg.exe and ffprobe.exe.";
            _ffStatus.Tag = "bad"; Theming.Apply(_ffStatus);
            return;
        }
        try
        {
            var caps = await FfmpegLocator.DetectAsync(loc);
            if (IsDisposed) return;
            if (caps == null)
            {
                _ffStatus.Text = $"ffprobe found at {loc.Folder}, but ffmpeg.exe is missing. Press \"Download FFmpeg\".";
                _ffStatus.Tag = "bad"; Theming.Apply(_ffStatus);
                return;
            }
            var line1 = $"FFmpeg found: version {caps.Version} at {loc.Folder}  (found via {loc.Source})";
            var line2 = $"x265: {YesNo(caps.X265)}  x264: {YesNo(caps.X264)}  NVENC listed: HEVC {YesNo(caps.NvencHevc)}, H.264 {YesNo(caps.Nvenc264)}";
            _ffStatus.Text = line1 + "\r\n" + line2 + "\r\nGPU (NVENC) test: running...";
            var nv = caps.NvencHevc ? await FfmpegLocator.TestNvencAsync(loc) : (false, "this build has no NVENC encoder");
            if (IsDisposed) return;
            _ffStatus.Text = line1 + "\r\n" + line2 + "\r\nGPU (NVENC) test: " + (nv.Item1 ? "works" : "NOT usable: " + nv.Item2);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { _ffStatus.Text = "Found " + loc.Folder + " but could not run it: " + ex.Message; _ffStatus.Tag = "bad"; Theming.Apply(_ffStatus); }
        }
    }

    // ---------- hardware ----------
    private void ShowHardware()
    {
        _hwText.Text = HardwareReport.Describe(_hw);
        _hwBenchText.Text = _benchUtc is DateTime t
            ? $"Last benchmark ({t.ToLocalTime():yyyy-MM-dd HH:mm}): CPU {(_benchCpu is double c ? c.ToString("0.0") + "x" : "not run")}, GPU {(_benchGpu is double g ? g.ToString("0.0") + "x" : "not run")} real time."
            : "No benchmark run yet. Time estimates use typical speeds.";
        _hwBench.Enabled = QueueEntries.Count > 0;
        if (_userModified.Count > 0)
            _hwText.Text += "\r\nYour own choices (kept when hardware is detected again): " + string.Join(", ", _userModified.Where(k => TunedSettings.All.Contains(k)));
    }

    /// <summary>Current window values as a Settings object (without closing the window).</summary>
    private Settings Current()
    {
        var cur = SettingsStore.Clone(_s);
        SaveTo(cur);
        return cur;
    }

    private async Task RedetectHardwareAsync()
    {
        if (_hwCts != null) return;
        _hwCts = new CancellationTokenSource();
        _hwDetect.Enabled = _hwBench.Enabled = _hwReset.Enabled = false;
        _hwText.Text = "Detecting hardware (about 5 seconds)...";
        try
        {
            var loc = FfmpegLocator.Find(_ffPath.Text.Trim());
            var hw = await Task.Run(() => HardwareDetector.DetectAsync(loc, null, _hwCts.Token));
            if (IsDisposed) return;
            _hw = hw;
            ApplyHardwareDecision(reset: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _hwText.Text = "Hardware detection failed: " + ex.Message; }
        finally
        {
            _hwCts?.Dispose(); _hwCts = null;
            if (!IsDisposed) { _hwDetect.Enabled = _hwReset.Enabled = true; _hwBench.Enabled = QueueEntries.Count > 0; }
        }
    }

    /// <summary>Applies the defaults for the profile and policy to the controls (settings changed by hand are kept unless reset).</summary>
    private void ApplyHardwareDecision(bool reset)
    {
        if (_hw == null) { _hwText.Text = "Not detected yet. Press \"Re-detect hardware\"."; return; }
        var cur = Current();   // marks what was changed in this window, relative to the baseline
        var d = HardwareDefaults.Decide(_hw, (DefaultsPolicy)Math.Max(0, _policy.SelectedIndex));
        if (reset) HardwareDefaults.ApplyAll(cur, d, _hw); else HardwareDefaults.Apply(cur, d, _hw);
        _userModified.Clear();
        foreach (var k in cur.UserModified) _userModified.Add(k);
        _x265Threads = cur.X265Threads;
        _loading = true;
        _rbGpu.Checked = cur.Mode == EncoderMode.Gpu; _rbCpu.Checked = cur.Mode == EncoderMode.Cpu;
        _preset.Text = cur.X265Preset; _gpuCq.Value = cur.GpuCq;
        _parGpu.Value = cur.ParallelGpuJobs; _parCpu.Value = cur.ParallelCpuJobs;
        _loading = false;
        _baseline = SettingsStore.Clone(Current());   // later edits are measured from here
        _baseline.UserModified = new List<string>(_userModified);
        ShowHardware();
    }

    private async Task RunBenchmarkAsync()
    {
        if (_hwCts != null) { _hwCts.Cancel(); return; }   // second click = cancel
        var cur = Current();
        var loc = FfmpegLocator.Find(_ffPath.Text.Trim());
        if (loc == null || loc.FfmpegPath.Length == 0) { _hwBenchText.Text = "ffmpeg was not found. Press Download FFmpeg first."; return; }
        var opts = cur.ToPlannerOptions();
        var sample = QueueEntries.Where(e => e.Probe.Video != null && (e.Probe.DurationSec ?? 0) >= 120)
            .Select(e => (Entry: e, Plan: Planner.Plan(e, opts))).FirstOrDefault(x => x.Plan.Action == PlanAction.ReencodeVideo);
        if (sample.Entry == null) { _hwBenchText.Text = "No file in the list needs a re-encode (and is longer than 2 minutes), so there is nothing to measure. Scan first."; return; }
        _hwCts = new CancellationTokenSource();
        _hwDetect.Enabled = _hwReset.Enabled = false;
        _hwBench.Text = "Cancel benchmark";
        _hwBenchText.Tag = null; Theming.Apply(_hwBenchText);
        _hwBenchText.Text = $"Encoding 20 seconds of {sample.Entry.FileName} in {(cur.Mode == EncoderMode.Gpu ? "GPU" : "CPU")} mode...";
        try
        {
            var r = await BenchmarkRunner.RunAsync(sample.Entry, sample.Plan, cur, loc, _hwCts.Token);
            if (IsDisposed) return;
            if (!r.Ok) { _hwBenchText.Text = r.Message; _hwBenchText.Tag = "bad"; Theming.Apply(_hwBenchText); return; }
            if (cur.Mode == EncoderMode.Gpu) _benchGpu = r.SpeedX; else _benchCpu = r.SpeedX;
            _benchUtc = DateTime.UtcNow;
            cur.BenchmarkCpuX = _benchCpu; cur.BenchmarkGpuX = _benchGpu;
            var jobs = QueueEntries.Where(e => (e.Probe.DurationSec ?? 0) > 0).Select(e => (Plan: Planner.Plan(e, opts), Dur: e.Probe.DurationSec ?? 0))
                .Where(x => x.Plan.WritesFile).Select(x => (x.Plan, x.Dur)).ToList();
            var hours = SpeedEstimator.EstimateHours(jobs, cur);
            _hwBenchText.Text = $"{(cur.Mode == EncoderMode.Gpu ? "GPU" : "CPU")} mode: {r.Message}. Estimated time for the {jobs.Count:N0} files that would be converted now: about {SpeedEstimator.Format(hours)}.";
        }
        catch (Exception ex) { if (!IsDisposed) { _hwBenchText.Text = "Benchmark failed: " + ex.Message; _hwBenchText.Tag = "bad"; Theming.Apply(_hwBenchText); } }
        finally
        {
            _hwCts?.Dispose(); _hwCts = null;
            if (!IsDisposed) { _hwBench.Text = "Run 20-second benchmark"; _hwDetect.Enabled = _hwReset.Enabled = true; }
        }
    }

    private async Task DownloadOlderAsync()
    {
        if (_ffCts != null) return;   // a download is running
        using var pick = new FfmpegVersionForm(_hw);
        if (pick.ShowDialog(this) != DialogResult.OK || pick.Selected == null) return;
        await DownloadFfmpegAsync(false, pick.Selected);
    }

    private async Task DownloadFfmpegAsync(bool older, FfmpegVersion? version = null)
    {
        if (_ffCts != null) { _ffCts.Cancel(); return; }   // second click = cancel
        _ffCts = new CancellationTokenSource();
        var oldText = _ffDownload.Text;
        _ffDownload.Text = "Cancel download";
        _ffDownloadOld.Enabled = _ffBrowse.Enabled = _ffDetect.Enabled = _ffPath.Enabled = false;
        _ffProgress.Value = 0; _ffProgress.Visible = true; _ffProgressText.Visible = true;
        _ffStatus.Tag = null; Theming.Apply(_ffStatus);
        _ffStatus.Text = "Downloading FFmpeg" + (version != null ? " " + version.Version : "") + "...";
        try
        {
            var folder = FfmpegDownloader.ChooseWritableFolder(FfmpegDownloader.DefaultPreferredFolder, FfmpegDownloader.DefaultFallbackFolder);
            if (version != null)
            {
                // an older version gets a folder of its own (ffmpeg\8.0.1) so versions never overwrite each other; already downloaded = just use it
                folder = FfmpegVersions.FolderFor(folder, version.Version);
                if (FfmpegVersions.IsInstalled(folder))
                {
                    _ffPath.Text = folder;
                    _ffProgressText.Text = $"FFmpeg {version.Version} was already downloaded to {folder}; it is used now.";
                    await DetectAsync();
                    return;
                }
            }
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (IsDisposed) return;
                if (p.Fraction is double f) { _ffProgress.Style = ProgressBarStyle.Continuous; _ffProgress.Value = (int)(f * 1000); }
                else _ffProgress.Style = ProgressBarStyle.Marquee;
                _ffProgressText.Text = p.Total is long t && p.Stage == "Downloading"
                    ? $"Downloading from {p.Source}: {p.Bytes / 1048576.0:0} of {t / 1048576.0:0} MB"
                    : p.Stage;
            });
            var r = await FfmpegDownloader.DownloadAsync(folder, older, progress, _ffCts.Token, sources: version != null ? new[] { FfmpegVersions.ToSource(version) } : null);
            if (IsDisposed) return;
            _ffPath.Text = r.Folder;
            _ffProgressText.Text = $"Done: ffmpeg.exe and ffprobe.exe extracted to {r.Folder} (from {r.Source}).";
            _ffProgress.Style = ProgressBarStyle.Continuous; _ffProgress.Value = _ffProgress.Maximum;
            await DetectAsync();
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) { _ffProgressText.Text = "Download cancelled."; _ffStatus.Text = "Download cancelled."; }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _ffProgress.Visible = false;
                _ffProgressText.Text = "";
                _ffStatus.Text = "Could not download FFmpeg: " + FfmpegDownloader.FriendlyError(ex) +
                                 "\r\nCheck the internet connection and try again, or download it yourself from gyan.dev and use Browse.";
                _ffStatus.Tag = "bad"; Theming.Apply(_ffStatus);
            }
        }
        finally
        {
            _ffCts?.Dispose(); _ffCts = null;
            if (!IsDisposed)
            {
                _ffDownload.Text = oldText;
                _ffDownloadOld.Enabled = _ffBrowse.Enabled = _ffDetect.Enabled = _ffPath.Enabled = true;
            }
        }
    }

    private static ThemeMode ThemeFromIndex(int i) => i == 1 ? ThemeMode.Light : i == 2 ? ThemeMode.Dark : ThemeMode.System;

    private static string YesNo(bool b) => b ? "OK" : "no";

    private static DateTime ParseTime(string s, int h, int m)
    {
        var d = DateTime.Today.AddHours(h).AddMinutes(m);
        return TimeSpan.TryParse(s, out var t) ? DateTime.Today.Add(t) : d;
    }

    // ---------- layout helpers ----------
    /// <summary>Width of the two columns at the default size, and the width below which the content scrolls sideways.</summary>
    internal const int DefaultContentWidth = 1010;
    internal const int MinContentWidth = 740;

    /// <summary>Wrapped text (status lines, hints) uses the width of its column, so it re-wraps when the window is resized.</summary>
    private static void FitWrappedLabels(Control root)
    {
        var width = Math.Max(240, root.Width / 2 - 70);
        foreach (var l in AllLabels(root))
            if (l.AutoSize && l.MaximumSize.Width > 0) l.MaximumSize = new Size(width, 0);
    }

    private static IEnumerable<Label> AllLabels(Control c)
    {
        foreach (Control ch in c.Controls)
        {
            if (ch is Label l) yield return l;
            foreach (var x in AllLabels(ch)) yield return x;
        }
    }

    private static NumericUpDown Num(int min, int max, int step = 1) => new() { Minimum = min, Maximum = max, Increment = step, Width = 80, TextAlign = HorizontalAlignment.Right };

    private static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    private static DateTimePicker TimePicker() => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 62 };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, Tag = "muted", MaximumSize = new Size(400, 0) };

    private static TableLayoutPanel Column()
    {
        var t = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static FlowLayoutPanel Flow(params Control[] c)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        f.Controls.AddRange(c);
        return f;
    }

    private static GroupBox G(string title, Action<TableLayoutPanel> fill)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(4, 2, 4, 4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // the control column takes the extra width (text boxes stretch)
        fill(t);
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 6), Margin = new Padding(3, 3, 3, 8), MinimumSize = new Size(350, 0), Dock = DockStyle.Fill };
        g.Controls.Add(t);
        return g;
    }

    private static void Row(TableLayoutPanel t, string? label, Control? c)
    {
        var r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label ?? "", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 12, 3) }, 0, r);
        if (c != null) { c.Anchor = c is TextBox ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left; c.Margin = new Padding(3, 2, 3, 2); t.Controls.Add(c, 1, r); }
    }

    private static void Span(TableLayoutPanel t, Control c)
    {
        var r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Margin = new Padding(3, 3, 3, 3);
        if (c is ProgressBar) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        t.Controls.Add(c, 0, r);
        t.SetColumnSpan(c, 2);
    }
}
