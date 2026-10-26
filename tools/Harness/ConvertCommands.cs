// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Convertr.Core;

namespace Convertr.Harness;

/// <summary>
/// Real end-to-end tests of the engine on SCRATCH COPIES (never the library):
///   convert --src DIR --work DIR --mode cpu|gpu [--keeppct N --keepmb N --only TEXT --container mp4|mkv] [--subs true]
///   quality --work DIR [--seconds N]        ssim / psnr / vmaf of each converted file against its original in the holding folder
///   undo    --work DIR                      restores every file of the journals in WORK\Logs
/// </summary>
internal static partial class Program
{
    private static string Opt(Dictionary<string, string> o, string k, string d = "") => o.GetValueOrDefault(k, d);

    private static void Robocopy(string src, string dst)
    {
        var psi = new ProcessStartInfo("robocopy", $"\"{src}\" \"{dst}\" /E /NFL /NDL /NJH /NJS /NP") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode >= 8) throw new IOException("robocopy failed: " + p.ExitCode);
    }

    private static async Task<int> ConvertAsync(Dictionary<string, string> o)
    {
        var src = Opt(o, "src"); var work = Opt(o, "work");
        if (src.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) || work.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refusing: Z: is read-only for tests"); return 2; }
        var mode = Opt(o, "mode", "gpu").Equals("cpu", StringComparison.OrdinalIgnoreCase) ? EncoderMode.Cpu : EncoderMode.Gpu;
        var s = new Settings
        {
            Mode = mode,
            MediaFolder = Path.Combine(work, "Media"),
            KeepIfSmallerPercent = int.Parse(Opt(o, "keeppct", "15")),
            KeepIfSmallerMb = int.Parse(Opt(o, "keepmb", "100")),
            Container = Opt(o, "container", "mp4").Equals("mkv", StringComparison.OrdinalIgnoreCase) ? OutputContainer.Mkv : OutputContainer.Mp4,
            KeepEmbeddedSubsIfNoExternal = Opt(o, "subs", "false") == "true",
            CleanMetadataOnSkipped = Opt(o, "cleanskipped", "true") == "true",
            Originals = OriginalsHandling.MoveToHolding,
        };
        if (o.TryGetValue("ffmpeg", out var ffdir)) s.FfmpegFolder = ffdir;
        if (o.TryGetValue("cq", out var cq)) s.GpuCq = int.Parse(cq);
        if (o.TryGetValue("crf", out var crf)) s.CpuCrf = int.Parse(crf);
        if (o.TryGetValue("legacycrf", out var lcrf)) s.LegacyCrf = int.Parse(lcrf);
        if (o.TryGetValue("preset", out var pr)) s.X265Preset = pr;
        var loc = FfmpegLocator.Find(s.FfmpegFolder);
        if (loc == null) { Console.WriteLine("ffmpeg not found"); return 1; }

        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        Console.WriteLine($"copying {src} -> {s.MediaFolder}");
        Robocopy(src, s.MediaFolder);
        var only = Opt(o, "only");
        if (only.Length > 0)
            foreach (var f in Directory.GetFiles(s.MediaFolder, "*", SearchOption.AllDirectories))
                if (!only.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(t => f.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) File.Delete(f);

        var cache = ProbeCache.Load(Path.Combine(work, "probe_cache.json"));
        var scanner = new Scanner(new FfprobeRunner(loc.FfprobePath), cache, s.ProbeThreads, false);
        var res = await scanner.ScanAsync(new[] { new LibraryRoot("Media", s.MediaFolder) }, null, CancellationToken.None);
        var rows = res.Entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).Select(e => (Entry: e, Plan: Planner.Plan(e, s.ToPlannerOptions()))).ToList();
        Console.WriteLine($"ffmpeg: {loc.FfmpegPath}");
        Console.WriteLine($"mode={mode} files={rows.Count} (convert/rewrite: {rows.Count(r => r.Plan.WritesFile)})");

        var logsDir = Path.Combine(work, "Logs");
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var log = new RunLog(RunLog.NewPath(logsDir, DateTime.Now)) { Echo = l => Console.WriteLine(l) };
        var journal = new UndoJournal(UndoJournal.NewPath(logsDir, DateTime.Now));
        var q = QueueBuilder.Build(rows, s, s.MediaFolder, runId);
        var eng = new ConversionEngine(s, new FfmpegProcessRunner(loc.FfmpegPath, s.ProcessPriority), new FfprobeRunner(loc.FfprobePath), log, journal, Path.Combine(work, "queue.json"));
        var lastPct = new Dictionary<int, int>();
        eng.ProgressChanged += p =>
        {
            if (p.Job == null) return;
            var pct = (int)(p.JobFraction * 100 / 10) * 10;
            if (lastPct.GetValueOrDefault(p.Job.Id, -1) != pct) { lastPct[p.Job.Id] = pct; Console.WriteLine($"   #{p.Job.Id} {p.Job.Entry.FileName}: {p.JobFraction:P0} speed {p.Speed:0.0}x eta {p.JobEta:hh\\:mm\\:ss} | overall {(p.TotalBytes > 0 ? (double)p.ProcessedBytes / p.TotalBytes : 0):P0} eta {p.OverallEta:hh\\:mm\\:ss}"); }
        };
        var sw = Stopwatch.StartNew();
        var sum = await eng.RunAsync(q);
        Console.WriteLine($"RUN DONE in {sw.Elapsed:hh\\:mm\\:ss}: done={sum.Done} notWorth={sum.NotWorthIt} failed={sum.Failed} skipped={sum.Skipped} pending={sum.Pending}");

        // per-file table
        var sb = new StringBuilder();
        sb.AppendLine("File,Mode,Action,Input MB,Output MB,Saving %,Would keep (15% and 100 MB),Seconds,Speed x,State,Verification,Timestamps preserved");
        foreach (var (e, p) in rows)
        {
            var j = q.Jobs.FirstOrDefault(x => x.Entry.FullPath == e.FullPath);
            var label = p.ActionLabel + (p.Kind == ReencodeKind.Compat ? " (compat)" : p.Kind == ReencodeKind.Size ? " (size)" : "") + (p.Deinterlace ? " +bwdif" : "");
            if (j == null) { sb.AppendLine(Csv.Line(new[] { e.FileName, mode.ToString(), label, (e.SizeBytes / 1048576.0).ToString("0.0"), "", "", "", "", "", "no file written", p.Reason, "" })); continue; }
            var saved = e.SizeBytes - j.OutputBytes;
            var keep = j.OutputBytes > 0 ? (j.SavingPercent >= 15 && saved >= 100L * 1048576 ? "yes" : "no") : "";
            var ts = j.State == JobState.Done ? (Math.Abs((File.GetLastWriteTimeUtc(j.FinalPath) - new DateTime(e.MtimeUtcTicks, DateTimeKind.Utc)).TotalSeconds) <= 2 ? "yes" : "NO") : "";
            sb.AppendLine(Csv.Line(new[] { e.FileName, mode.ToString(), label, (e.SizeBytes / 1048576.0).ToString("0.0"), j.OutputBytes > 0 ? (j.OutputBytes / 1048576.0).ToString("0.0") : "", j.OutputBytes > 0 ? j.SavingPercent.ToString("0.0") : "", keep, j.ElapsedSeconds.ToString("0"), j.Speed?.ToString("0.0") ?? "", j.State.ToString(), j.Message, ts }));
        }
        var csvPath = Path.Combine(Opt(o, "out", work), $"convert_{mode.ToString().ToLowerInvariant()}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        File.WriteAllText(csvPath, sb.ToString());
        Console.WriteLine("table: " + csvPath);
        Console.WriteLine(sb.ToString());
        var leftovers = Directory.GetFiles(s.MediaFolder, "*convertr-part*", SearchOption.AllDirectories);
        Console.WriteLine("leftover temporary files: " + leftovers.Length);
        return sum.Failed == 0 && leftovers.Length == 0 ? 0 : 3;
    }

    private static string Run(string exe, IEnumerable<string> args, out int exit)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        exit = p.ExitCode;
        return outp.Result + "\n" + err.Result;
    }

    private static Task<int> QualityAsync(Dictionary<string, string> o)
    {
        var work = Opt(o, "work");
        var seconds = Opt(o, "seconds", "60");
        var loc = FfmpegLocator.Find(Opt(o, "ffmpeg"));
        if (loc == null) return Task.FromResult(1);
        var items = UndoService.Load(Directory.GetFiles(Path.Combine(work, "Logs"), "undo_*.csv"));
        var sb = new StringBuilder("File,SSIM (All),PSNR avg dB,VMAF mean,Output MB,Original MB\n");
        foreach (var it in items)
        {
            if (!File.Exists(it.NewPath) || !File.Exists(it.HoldingPath)) continue;
            var dur = ProbeParser.Parse(Run(loc.FfprobePath, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", it.NewPath }, out _), null).DurationSec ?? 600;
            var start = Math.Max(0, dur / 2 - double.Parse(seconds) / 2);
            var ss = start.ToString("0.##", CultureInfo.InvariantCulture);
            var interlaced = ProbeParser.Parse(Run(loc.FfprobePath, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", it.HoldingPath }, out _), null).Video?.Interlaced == true;
            var fpsText = Run(loc.FfprobePath, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", "-i", it.NewPath }, out _).Trim().Split('\n')[0].Trim();
            if (!Regex.IsMatch(fpsText, @"^\d+(/\d+)?$")) fpsText = "24000/1001";
            // frames are compared by number (the two files have different time bases, which misaligns plain timestamps)
            var norm = $"format=yuv420p,fps={fpsText},setpts=N/({fpsText})/TB";
            var refChain = (interlaced ? "bwdif=mode=send_frame:parity=auto:deint=all," : "") + $"fps={fpsText},format=yuv420p,setpts=N/({fpsText})/TB";
            var fc = $"[0:v]{norm},split=3[d1][d2][d3];[1:v]{refChain},split=3[r1][r2][r3];[d1][r1]ssim[s0];[d2][r2]psnr[p0];[d3][r3]libvmaf=n_threads=8[v0]";
            var text = Run(loc.FfmpegPath, new[] { "-hide_banner", "-nostdin", "-ss", ss, "-t", seconds, "-i", it.NewPath, "-ss", ss, "-t", seconds, "-i", it.HoldingPath, "-filter_complex", fc, "-map", "[s0]", "-f", "null", "-", "-map", "[p0]", "-f", "null", "-", "-map", "[v0]", "-f", "null", "-" }, out var code);
            string G(string rx) { var m = Regex.Match(text, rx); return m.Success ? m.Groups[1].Value : "n/a"; }
            var row = Csv.Line(new[] { Path.GetFileName(it.NewPath), G(@"SSIM .*?All:([0-9.]+)"), G(@"PSNR .*?average:([0-9.]+)"), G(@"VMAF score: ([0-9.]+)"), (new FileInfo(it.NewPath).Length / 1048576.0).ToString("0.0"), (new FileInfo(it.HoldingPath).Length / 1048576.0).ToString("0.0") });
            Console.WriteLine(row + (code != 0 ? "   (ffmpeg exit " + code + ")" : ""));
            if (code != 0) Console.WriteLine(text.Length > 1500 ? text[^1500..] : text);
            sb.AppendLine(row);
        }
        var path = Path.Combine(Opt(o, "out", work), "quality_" + Path.GetFileName(work.TrimEnd('\\', '/')) + ".csv");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("quality table: " + path);
        return Task.FromResult(0);
    }

    private static int UndoRun(Dictionary<string, string> o)
    {
        var work = Opt(o, "work");
        var logs = Path.Combine(work, "Logs");
        var journals = Directory.GetFiles(logs, "undo_*.csv");
        var items = UndoService.Load(journals);
        Console.WriteLine($"{items.Count} undoable entries, {items.Count(i => i.CanUndo)} can be undone");
        var log = new RunLog(Path.Combine(logs, "undo_run.log")) { Echo = Console.WriteLine };
        var res = UndoService.Execute(items.Where(i => i.CanUndo), new UndoJournal(UndoJournal.NewPath(logs, DateTime.Now.AddSeconds(1))), log);
        foreach (var r in res)
        {
            var ok = r.Success && File.Exists(r.Item.OriginalPath) && Math.Abs((File.GetLastWriteTimeUtc(r.Item.OriginalPath) - r.Item.OrigMtimeUtc).TotalSeconds) <= 2 && new FileInfo(r.Item.OriginalPath).Length == r.Item.OrigSize;
            Console.WriteLine($"{(ok ? "RESTORED OK" : "PROBLEM")}: {r.Item.OriginalPath} ({r.Message})");
        }
        return res.All(r => r.Success) ? 0 : 3;
    }
}

internal static partial class Program
{
    /// <summary>runshots --media FOLDER --out DIR --cache FILE: main window mid-run, summary dialog, undo dialog, Setup with FFmpeg download progress (Light and Dark).</summary>
    private static int RunShots(Dictionary<string, string> o)
    {
        ApplicationConfiguration.Initialize();
        var outDir = Opt(o, "out", ".");
        var media = Opt(o, "media", HarnessDefaultMedia);
        var cachePath = Opt(o, "cache", Path.Combine(outDir, "probe_cache.json"));
        var baseSettings = new Settings { MediaFolder = media };
        var cache = ProbeCache.Load(cachePath);
        var res = new Scanner(new NoProbeRunner(), cache, 3, false).ScanAsync(new[] { new LibraryRoot(Leaf(media), media) }, null, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"entries={res.Entries.Count}");
        foreach (var (theme, suffix) in new[] { (ThemeMode.Light, ""), (ThemeMode.Dark, "_dark") })
        {
            var settings = SettingsStore.Clone(baseSettings);
            settings.Theme = theme;
            Theming.SetMode(theme);
            var tmp = Path.Combine(outDir, "settings_shots.json");
            SettingsStore.Save(settings, tmp);
            string N(string name) => Path.Combine(outDir, name + suffix + ".png");

            using (var main = new MainForm(tmp, Path.Combine(outDir, "unused_cache.json")))
            {
                main.StartPosition = FormStartPosition.Manual; main.Location = new Point(10, 10);
                main.ClientSize = new Size(1500, 820);
                main.Show(); Pump(300);
                main.LoadEntries(res.Entries, "Scan finished.");
                Pump(300);
                foreach (Control c in AllControls(main))
                    if (c is ComboBox cb && cb.Items.Count > 3 && cb.Items[1]?.ToString()?.StartsWith("Re-encode video") == true) cb.SelectedIndex = 2;
                Pump(300);
                main.Grid.Sort(main.Grid.Columns["EstSaveMidBytes"]!, System.ComponentModel.ListSortDirection.Descending);
                Pump(300);
                var shown = main.Grid.Rows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem as PlanRow).Where(r => r != null).Cast<PlanRow>().Take(9).ToList();

                // summary dialog (the real text for these 9 files)
                var (heading, body) = main.PreviewRunSummary(shown);
                using (var dlg = new ConfirmRunForm(heading, body, false))
                {
                    dlg.StartPosition = FormStartPosition.Manual; dlg.Location = new Point(10, 10);
                    dlg.Show(); Pump(500);
                    Snap(dlg, N("confirm_dialog"));
                    dlg.Close();
                }

                // main window in the middle of a run
                var q = new QueueFile { RunId = "shot", MediaFolder = media };
                var id = 1;
                foreach (var r in shown) q.Jobs.Add(new QueueJob { Id = id++, Entry = r.Entry, Plan = r.Plan, FinalPath = r.Entry.FullPath });
                q.Jobs[0].State = JobState.Done; q.Jobs[0].OutputBytes = (long)(q.Jobs[0].Entry.SizeBytes * 0.58); q.Jobs[0].SavingPercent = 42;
                q.Jobs[1].State = JobState.Done; q.Jobs[1].OutputBytes = (long)(q.Jobs[1].Entry.SizeBytes * 0.64); q.Jobs[1].SavingPercent = 36;
                q.Jobs[2].State = JobState.NotWorthIt; q.Jobs[2].OutputBytes = (long)(q.Jobs[2].Entry.SizeBytes * 0.93); q.Jobs[2].SavingPercent = 7;
                q.Jobs[3].State = JobState.Encoding;
                var total = q.Jobs.Sum(j => j.Entry.SizeBytes);
                var done = q.Jobs.Take(3).Sum(j => j.Entry.SizeBytes);
                var prog = new EngineProgress(q.Jobs[3], 0.41, 1.9, TimeSpan.FromMinutes(23), 3, q.Jobs.Count, done + (long)(q.Jobs[3].Entry.SizeBytes * 0.41), total, TimeSpan.FromMinutes(52), TimeSpan.FromMinutes(2 * 60 + 14));
                main.ShowRunState(q, prog);
                Pump(400);
                Snap(main, N("main_window_running"));
                main.EndRunState();
                main.Close();
            }

            // undo dialog
            var items = new List<UndoItem>();
            foreach (var r in res.Entries.Where(e => Planner.Plan(e, settings.ToPlannerOptions()).Kind == ReencodeKind.Size).Take(6))
                items.Add(new UndoItem("20261016_2318", "x", r.FullPath, r.FullPath + ".orig", Path.ChangeExtension(r.FullPath, ".mp4"), r.SizeBytes, (long)(r.SizeBytes * 0.6), DateTime.UtcNow, DateTime.UtcNow, media, "MoveToHolding", items.Count != 4, items.Count != 4 ? "Can undo" : "Cannot undo (original purged or missing)"));
            using (var u = new UndoForm(items))
            {
                u.StartPosition = FormStartPosition.Manual; u.Location = new Point(10, 10);
                u.Show(); Pump(500);
                Console.WriteLine($"undo grid selectedRows={u.Controls.OfType<DataGridView>().First().SelectedRows.Count}");
                Snap(u, N("undo_dialog"));
                u.Close();
            }

            // setup with a download in progress
            using (var s = new SetupForm(settings))
            {
                s.StartPosition = FormStartPosition.Manual; s.Location = new Point(10, 10);
                s.Show(); Pump(3500);
                s.ClientSize = new Size(1050, 1080);
                var pb = AllControls(s).OfType<ProgressBar>().First();
                var lbl = AllControls(s).OfType<Label>().First(l => l.Visible == false && l.Tag as string == "muted");
                pb.Visible = true; pb.Value = 430; lbl.Visible = true; lbl.Text = "Downloading from gyan.dev (release essentials): 47 of 109 MB";
                Pump(400);
                Snap(s, N("setup_window_ffmpeg_download"));
                pb.Visible = false; lbl.Visible = false;
                Pump(200);
                Snap(s, N("setup_window_ffmpeg"));
                s.Close();
            }
        }
        return 0;
    }
}
