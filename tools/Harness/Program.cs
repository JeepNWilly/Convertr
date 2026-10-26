// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Convertr;
using Convertr.Core;

namespace Convertr.Harness;

/// <summary>
/// Developer harness (never part of the shipped exe).
///   scan   --media FOLDER --out DIR [--cache FILE] [--compare library_formats.csv]   headless real scan of ONE folder: before/after listing, counts, comparison
///   shots  --media FOLDER --out DIR --cache FILE                                     renders MainForm / SetupForm / DetailsForm (Light and Dark) to PNG with DrawToBitmap
/// </summary>
internal static partial class Program
{
    /// <summary>Default folder for the developer harness only (it is not part of the shipped exe, which has no built-in paths).</summary>
    private const string HarnessDefaultMedia = @"D:\Media\Movies";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: scan|shots --out DIR [--cache FILE] [--compare CSV]"); return 2; }
        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
        var outDir = opt.GetValueOrDefault("out", Path.Combine(AppContext.BaseDirectory, "out"));
        var cache = opt.GetValueOrDefault("cache", Path.Combine(outDir, "probe_cache.json"));
        Directory.CreateDirectory(outDir);
        MainForm.DisableHardwareAutoDetect = true;   // pictures and tests must not change settings
        var plex = opt.GetValueOrDefault("plexversions", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        var allowIl = opt.GetValueOrDefault("processinterlaced", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        var media = opt.GetValueOrDefault("media", HarnessDefaultMedia);
        return args[0].ToLowerInvariant() switch
        {
            "scan" => ScanAsync(outDir, cache, opt.GetValueOrDefault("compare"), plex, allowIl, media).GetAwaiter().GetResult(),
            "shots" => Shots(outDir, cache, media),
            "convert" => ConvertAsync(opt).GetAwaiter().GetResult(),
            "undo" => UndoRun(opt),
            "quality" => QualityAsync(opt).GetAwaiter().GetResult(),
            "runshots" => RunShots(opt),
            "hardware" => HardwareRun(opt, outDir),
            "launch" => LaunchCheck(opt, outDir),
            "savings" => SavingsFromCache(opt, outDir),
            "setupshots" => SetupShots(opt, outDir),
            "versionshots" => VersionShots(opt, outDir),
            _ => 2,
        };
    }

    // ---------------- listing snapshot (proves zero writes to the library) ----------------
    private static (int Files, long Bytes, string Hash) Snapshot(IEnumerable<LibraryRoot> roots)
    {
        var lines = new List<string>();
        long bytes = 0;
        foreach (var r in roots)
            foreach (var (full, len, mt) in Scanner.EnumerateFiles(r.Path.TrimEnd('\\', '/'), new HashSet<string>()))
            { lines.Add($"{full}|{len}|{mt}"); bytes += len; }
        lines.Sort(StringComparer.Ordinal);
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        return (lines.Count, bytes, h);
    }

    private static async Task<int> ScanAsync(string outDir, string cachePath, string? compareCsv, bool includePlexVersions, bool allowInterlaced, string media)
    {
        var settings = new Settings { IncludePlexVersions = includePlexVersions, ProcessInterlaced = allowInterlaced, MediaFolder = media };
        Console.WriteLine($"media folder: {media}");
        Console.WriteLine($"options: IncludePlexVersions={includePlexVersions} ProcessInterlaced={allowInterlaced}");
        var roots = new List<LibraryRoot> { new(Leaf(settings.MediaFolder), settings.MediaFolder) };
        var loc = FfmpegLocator.Find(settings.FfmpegFolder);
        if (loc == null) { Console.WriteLine("ffprobe not found"); return 1; }
        Console.WriteLine($"ffprobe: {loc.FfprobePath} (found via {loc.Source})");

        var before = Snapshot(roots);
        Console.WriteLine($"BEFORE listing: {before.Files} files, {before.Bytes:N0} bytes, sha256 {before.Hash}");

        var cache = ProbeCache.Load(cachePath);
        Console.WriteLine($"cache entries at start: {cache.Count}");
        var scanner = new Scanner(new FfprobeRunner(loc.FfprobePath), cache, settings.ProbeThreads, settings.IncludePlexVersions);
        var last = 0;
        var prog = new SyncProgress(p =>
        {
            if (p.Phase == ScanPhase.Probing && p.Done / 250 != last / 250) { last = p.Done; Console.WriteLine($"  probing {p.Done}/{p.Total} (cache {p.FromCache}, failed {p.Failed})"); }
        });
        var res = await scanner.ScanAsync(roots, prog, CancellationToken.None);
        Console.WriteLine($"scan: cancelled={res.Cancelled} enumerated={res.Enumerated} entries={res.Entries.Count} cacheHits={res.CacheHits} probed={res.Probed} failed={res.Failed} elapsed={res.Elapsed}");

        var after = Snapshot(roots);
        Console.WriteLine($"AFTER  listing: {after.Files} files, {after.Bytes:N0} bytes, sha256 {after.Hash}");
        Console.WriteLine(before == after ? "LIBRARY UNCHANGED: before/after listings are identical (path, size, mtime)" : "!!! LIBRARY LISTING DIFFERS !!!");

        var opts = settings.ToPlannerOptions();
        var plans = res.Entries.Select(e => (Entry: e, Plan: Planner.Plan(e, opts))).ToList();
        var s = PlanSummary.From(plans);
        var sb = new StringBuilder();
        void P(string line) { Console.WriteLine(line); sb.AppendLine(line); }
        P("");
        P($"files={s.Total}  totalGB={s.TotalBytes / 1073741824.0:N0}  ({s.TotalBytes / 1099511627776.0:0.00} TB)");
        P($"Re-encode size={s.ReencodeSize}  Re-encode compat={s.ReencodeCompat}  Fix audio only={s.FixAudio}  Remux={s.Remux}  Skip={s.Skip}  Review={s.Review}  Error={s.Error}");
        P($"audio conversions (size+fix+compat files that also convert audio)={s.AudioConversions}; size/fix only={plans.Count(x => x.Plan.AudioConversion && x.Plan.Kind != ReencodeKind.Compat)}");
        P($"size candidates: {s.SizeCandidateBytes / 1073741824.0:N0} GB in {s.ReencodeSize}");
        SavingsReport(res.Entries, opts, P);
        P($"warnings={s.Warnings}");
        P("by library: " + string.Join(", ", res.Entries.GroupBy(e => e.Library).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var g in plans.GroupBy(x => x.Plan.ActionLabel + (x.Plan.Kind == ReencodeKind.Compat ? " (compat)" : x.Plan.Kind == ReencodeKind.Size ? " (size)" : "")).OrderBy(g => g.Key))
            P($"  {g.Key,-28} {g.Count(),5}   {g.Sum(x => x.Entry.SizeBytes) / 1073741824.0,8:N1} GB");
        foreach (var x in plans.Where(x => x.Plan.Action is PlanAction.Error or PlanAction.Review))
            P($"  {x.Plan.ActionLabel}: {x.Entry.Library}\\{x.Entry.RelativePath} :: {x.Plan.Reason}");
        P("bitrate sources: " + string.Join(", ", res.Entries.Where(e => e.Probe.Video != null).GroupBy(e => e.Probe.Video!.KbpsSource).Select(g => $"{(g.Key == "" ? "unknown" : g.Key)}={g.Count()}")));
        P("metadata: " + $"clean={plans.Count(x => x.Plan.Metadata.IsClean)} junk={plans.Count(x => !x.Plan.Metadata.IsClean)}  (of rewriteable: {plans.Count(x => x.Plan.WritesFile)}; junk among those: {plans.Count(x => x.Plan.WritesFile && !x.Plan.Metadata.IsClean)})");
        P("interlaced files: " + res.Entries.Count(e => e.Probe.Video?.Interlaced == true));

        PlanCsv.Write(Path.Combine(outDir, "plan_real.csv"), plans);
        File.WriteAllText(Path.Combine(outDir, "scan_summary.txt"), sb.ToString());

        if (compareCsv != null && File.Exists(compareCsv)) Compare(res.Entries, compareCsv, outDir);
        return before == after && !res.Cancelled ? 0 : 3;
    }

    /// <summary>Projected saving per mode, library part and source bitrate bracket (the keep-if-smaller effect is included).</summary>
    private static void SavingsReport(List<ScanEntry> entries, PlannerOptions opts, Action<string> P)
    {
        foreach (var mode in new[] { EncoderMode.Gpu, EncoderMode.Cpu })
        {
            var mo = opts with { Mode = mode };
            var mp = entries.Select(e => (Entry: e, Plan: Planner.Plan(e, mo))).ToList();
            foreach (var grp in new[] { "Movies", "Series", "Total" })
            {
                var part = grp == "Total" ? mp : mp.Where(x => x.Entry.RelativePath.StartsWith(grp + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
                var sm = PlanSummary.From(part);
                P($"projected saving {mode,-3} {grp,-6}: candidates {sm.ReencodeSize,4} ({sm.SizeCandidateBytes / 1073741824.0,6:N0} GB)  low {sm.EstSaveLowBytes / 1073741824.0,5:N0}  likely {sm.EstSaveMidBytes / 1073741824.0,5:N0}  high {sm.EstSaveHighBytes / 1073741824.0,5:N0} GB   likely refused {sm.LikelyRefused,3} ({sm.LikelyRefusedBytes / 1073741824.0,5:N0} GB)");
            }
            // by resolution and source bitrate bracket
            foreach (var g in mp.Where(x => x.Plan.Action == PlanAction.ReencodeVideo && x.Plan.Kind == ReencodeKind.Size)
                         .GroupBy(x => $"{ResolutionClassifier.Label(x.Entry.Probe.Video!.Resolution),-5} {((x.Entry.Probe.Video!.Kbps ?? 0) / 1500) * 1.5:0.0}-{((x.Entry.Probe.Video!.Kbps ?? 0) / 1500 + 1) * 1.5:0.0} Mbps").OrderBy(g => g.Key))
                P($"   {mode} {g.Key}: n={g.Count(),3}  {g.Sum(x => x.Entry.SizeBytes) / 1073741824.0,6:N0} GB  likely saving {g.Sum(x => x.Plan.EstSaveMidBytes) / 1073741824.0,5:N0} GB ({100.0 * g.Sum(x => x.Plan.EstSaveMidBytes) / Math.Max(1, g.Sum(x => x.Entry.SizeBytes)):0}%)  refused {g.Count(x => x.Plan.LikelyRefused)}");
        }
    }

    /// <summary>savings --cache probe_cache.json --root D:\...\Media --out DIR: the same report from the local probe cache only (no access to the library).</summary>
    private static int SavingsFromCache(Dictionary<string, string> o, string outDir)
    {
        var cache = ProbeCache.Load(o.GetValueOrDefault("cache", ""));
        var root = o.GetValueOrDefault("root", HarnessDefaultMedia).TrimEnd('\\', '/');
        var entries = cache.Snapshot().Where(x => x.Path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(x => new ScanEntry { Library = "Media", FullPath = x.Path, RelativePath = x.Path[(root.Length + 1)..], SizeBytes = x.Size, MtimeUtcTicks = x.MtimeTicks, Probe = x.Info }).ToList();
        var sb = new StringBuilder();
        void P(string line) { Console.WriteLine(line); sb.AppendLine(line); }
        P($"cache entries under {root}: {entries.Count}");
        var opts = new Settings().ToPlannerOptions();
        var s = PlanSummary.From(entries.Select(e => (e, Planner.Plan(e, opts))));
        P($"Re-encode size={s.ReencodeSize}  compat={s.ReencodeCompat}  fix audio={s.FixAudio}  remux={s.Remux}  skip={s.Skip}  review={s.Review}  error={s.Error}");
        P($"size candidates: {s.SizeCandidateBytes / 1073741824.0:N0} GB in {s.ReencodeSize}");
        SavingsReport(entries, opts, P);
        File.WriteAllText(Path.Combine(outDir, "savings_report.txt"), sb.ToString());
        return 0;
    }

    private static string Leaf(string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
        return leaf.Length > 0 ? leaf : path;
    }

    private static readonly Regex AudioRx = new(@"^(\S+) ([a-z0-9_]+)(\(.*\))? (\d+)ch$");

    private static void Compare(List<ScanEntry> entries, string csvPath, string outDir)
    {
        var group = entries.Count > 0 ? entries[0].Library : "";
        var rows = CsvReader.Read(csvPath).Where(r => r["Library"].Equals(group, StringComparison.OrdinalIgnoreCase)).ToList();
        var byKey = entries.ToDictionary(e => e.Library + "|" + e.RelativePath, StringComparer.OrdinalIgnoreCase);
        var diffs = new List<string>();
        var matched = 0;
        foreach (var r in rows)
        {
            var key = r["Library"] + "|" + r["RelativePath"];
            if (!byKey.TryGetValue(key, out var e)) { diffs.Add("MISSING in new scan: " + key); continue; }
            matched++;
            var p = e.Probe; var v = p.Video;
            void D(string field, string a, string b) { if (a != b) diffs.Add($"{key}: {field} csv='{a}' new='{b}'"); }
            D("Container", r["Container"], p.FormatName ?? "");
            D("VideoCodec", r["VideoCodec"], v?.Codec ?? "");
            D("Width", r["Width"], v?.Width.ToString() ?? "");
            D("Height", r["Height"], v?.Height.ToString() ?? "");
            D("Resolution", r["Resolution"], v == null ? "" : ResolutionClassifier.Label(v.Resolution));
            D("BitDepth", r["BitDepth"], v?.BitDepth.ToString() ?? "");
            D("VideoBitrateKbps", r["VideoBitrateKbps"], v?.Kbps?.ToString() ?? "");
            D("VBRSource", r["VBRSource"], v?.KbpsSource ?? "");
            D("AudioTracks", r["AudioTracks"], p.Audio.Count.ToString());
            D("SubTracks", r["SubTracks"], p.Subs.Count.ToString());
            D("ImageSubs", r["ImageSubs"], p.Subs.Count(s => s.IsImage).ToString());
            D("ExternalSubs>0", (r["ExternalSubs"] != "0" && r["ExternalSubs"] != "").ToString(), e.HasExternalSubs.ToString());
            var csvAudio = string.Join(";", r["AudioSummary"].Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(a => { var m = AudioRx.Match(a); return m.Success ? m.Groups[2].Value + "/" + m.Groups[4].Value : a; }));
            D("Audio", csvAudio, string.Join(";", p.Audio.Select(a => a.Codec + "/" + a.Channels)));
            var csvSize = double.Parse(r["SizeMB"], CultureInfo.InvariantCulture);
            if (Math.Abs(csvSize - Math.Round(e.SizeBytes / 1048576.0, 1)) > 0.11) diffs.Add($"{key}: SizeMB csv={csvSize} new={e.SizeBytes / 1048576.0:0.0}");
        }
        var csvKeys = new HashSet<string>(rows.Select(r => r["Library"] + "|" + r["RelativePath"]), StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) if (!csvKeys.Contains(e.Library + "|" + e.RelativePath)) diffs.Add("EXTRA in new scan: " + e.Library + "|" + e.RelativePath);
        Console.WriteLine($"\nCOMPARE vs {Path.GetFileName(csvPath)}: csv rows={rows.Count}, new entries={entries.Count}, matched by path={matched}, field differences={diffs.Count}");
        foreach (var d in diffs.Take(40)) Console.WriteLine("  " + d);
        File.WriteAllLines(Path.Combine(outDir, "compare_diffs.txt"), diffs);
    }

    private sealed class SyncProgress : IProgress<ScanProgress>
    {
        private readonly Action<ScanProgress> _a;
        public SyncProgress(Action<ScanProgress> a) { _a = a; }
        public void Report(ScanProgress value) { lock (this) _a(value); }
    }

    // ---------------- screenshots ----------------
    private sealed class NoProbeRunner : IProbeRunner
    {
        public Task<ProbeRunResult> RunAsync(string path, CancellationToken ct) => Task.FromResult(new ProbeRunResult("", "not cached", 1, false));
    }

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(30); }
    }

    /// <summary>DrawToBitmap does not paint the selected text of drop-down-list combo boxes; render them as editable boxes for the picture only.</summary>
    private static void FixCombos(Control root)
    {
        foreach (var c in AllControls(root).OfType<ComboBox>().ToList())
        {
            if (c.DropDownStyle != ComboBoxStyle.DropDownList) continue;
            var idx = c.SelectedIndex;
            c.DropDownStyle = ComboBoxStyle.DropDown;
            c.SelectedIndex = idx;
            c.SelectionStart = 0; c.SelectionLength = 0;
        }
    }

    private static void Snap(Form f, string path)
    {
        FixCombos(f);
        Pump(400);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine("saved " + path + $" ({f.Width}x{f.Height})");
    }

    private static int Shots(string outDir, string cachePath, string media)
    {
        ApplicationConfiguration.Initialize();
        var baseSettings = new Settings { MediaFolder = media };
        var roots = new List<LibraryRoot> { new(Leaf(media), media) };
        var cache = ProbeCache.Load(cachePath);
        Console.WriteLine("media folder: " + media + "   cache entries: " + cache.Count);
        var res = new Scanner(new NoProbeRunner(), cache, 3, baseSettings.IncludePlexVersions).ScanAsync(roots, null, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"entries={res.Entries.Count} cacheHits={res.CacheHits} unprobed={res.Probed}");
        var ok = true;
        ok &= RenderSet(outDir, baseSettings, res, ThemeMode.Light, "");
        ok &= RenderSet(outDir, baseSettings, res, ThemeMode.Dark, "_dark");
        // the Light set is the default look: keep an explicit *_light copy of the two headline pictures
        File.Copy(Path.Combine(outDir, "main_window.png"), Path.Combine(outDir, "main_window_light.png"), true);
        File.Copy(Path.Combine(outDir, "setup_window.png"), Path.Combine(outDir, "setup_window_light.png"), true);
        Console.WriteLine(ok ? "SELECTION CHECK OK: nothing selected in grids/dialogs (Light and Dark)" : "!!! SELECTION CHECK FAILED");
        return ok ? 0 : 4;
    }

    private static bool RenderSet(string outDir, Settings baseSettings, ScanResult res, ThemeMode theme, string suffix)
    {
        var ok = true;
        var settings = SettingsStore.Clone(baseSettings);
        settings.Theme = theme;
        Theming.SetMode(theme);
        var tmpSettings = Path.Combine(outDir, "settings_shots.json");
        SettingsStore.Save(settings, tmpSettings);
        Console.WriteLine($"--- theme {theme} (IsDark={Theming.IsDark})");
        string N(string name) => Path.Combine(outDir, name + suffix + ".png");

        // main window
        using (var main = new MainForm(tmpSettings, Path.Combine(outDir, "unused_cache.json")))
        {
            main.StartPosition = FormStartPosition.Manual;
            main.Location = new Point(10, 10);
            main.ClientSize = new Size(1500, 820);
            main.Show();
            Pump(300);
            main.LoadEntries(res.Entries, $"Scan finished: {res.Entries.Count:N0} files ({res.CacheHits:N0} from cache). Nothing was changed.");
            Pump(500);
            Console.WriteLine($"main grid rows={main.Grid.Rows.Count} selectedRows={main.Grid.SelectedRows.Count} selectedCells={main.Grid.SelectedCells.Count} currentCell={(main.Grid.CurrentCell == null ? "null" : "SET")}");
            ok &= main.Grid.SelectedRows.Count == 0 && main.Grid.CurrentCell == null && main.Grid.SelectedCells.Count == 0;
            Snap(main, N("main_window"));

            // narrow window: the status lines must wrap, not clip
            main.ClientSize = new Size(760, 820);
            Pump(500);
            Snap(main, N("main_window_narrow"));
            main.ClientSize = new Size(1500, 820);
            Pump(400);

            // filtered view: size re-encodes, sorted by estimated savings (descending)
            foreach (Control c in AllControls(main))
                if (c is ComboBox cb && cb.Items.Count > 3 && cb.Items[1]?.ToString()?.StartsWith("Re-encode video") == true) cb.SelectedIndex = 2;
            Pump(300);
            var col = main.Grid.Columns["EstSaveMidBytes"]!;
            main.Grid.Sort(col, System.ComponentModel.ListSortDirection.Descending);
            Pump(300);
            Console.WriteLine($"filtered grid rows={main.Grid.Rows.Count} selectedRows={main.Grid.SelectedRows.Count} currentCell={(main.Grid.CurrentCell == null ? "null" : "SET")}");
            ok &= main.Grid.SelectedRows.Count == 0 && main.Grid.CurrentCell == null;
            Snap(main, N("main_window_size_candidates"));
            main.Close();
        }

        // details dialog for the first legacy file
        var legacy = res.Entries.First(e => Planner.Plan(e, settings.ToPlannerOptions()).Kind == ReencodeKind.Compat);
        using (var d = new DetailsForm(legacy.FileName, new PlanRow(legacy, Planner.Plan(legacy, settings.ToPlannerOptions())).Details(settings)))
        {
            d.StartPosition = FormStartPosition.Manual; d.Location = new Point(10, 10);
            d.Show(); Pump(500);
            foreach (Control c in AllControls(d)) if (c is TextBox t) { Console.WriteLine($"details textbox selection length={t.SelectionLength}"); ok &= t.SelectionLength == 0; }
            Snap(d, N("details_dialog"));
            d.Close();
        }

        // setup
        using (var s = new SetupForm(settings))
        {
            s.StartPosition = FormStartPosition.Manual; s.Location = new Point(10, 10);
            s.Show(); Pump(2500);
            s.ClientSize = new Size(1050, 1080);
            Pump(300);
            foreach (Control c in AllControls(s)) if (c is TextBox t) ok &= t.SelectionLength == 0;
            Snap(s, N("setup_window"));
            s.Close();
        }
        return ok;
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in AllControls(c)) yield return d;
        }
    }
}
