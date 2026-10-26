// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.


using System.Drawing;
using System.Text.Json;
using Convertr;
using Convertr.Core;

namespace Convertr.Harness;

internal static partial class Program
{
    /// <summary>
    /// hardware --out DIR [--ffmpeg FOLDER] [--shots true] [--bench FILE] [--policy auto|cpu|gpu]
    /// Real hardware detection on this computer (CPU, GPU, driver, NVENC test), the defaults decision, optional Setup screenshots
    /// (Light and Dark) and an optional 20-second benchmark of FILE (a scratch file; Z: is refused).
    /// </summary>
    /// <summary>launch --out DIR: starts a MainForm with an empty temporary settings.json (first launch) and waits for the background detection.</summary>
    private static int LaunchCheck(Dictionary<string, string> o, string outDir)
    {
        ApplicationConfiguration.Initialize();
        MainForm.DisableHardwareAutoDetect = false;
        var path = Path.Combine(outDir, "settings_launch.json");
        if (File.Exists(path)) File.Delete(path);
        var seed = new Settings();
        if (o.ContainsKey("usermode")) { seed.Mode = EncoderMode.Gpu; seed.UserModified.Add(TunedSettings.Mode); }   // user chose GPU by hand
        if (o.TryGetValue("ffmpeg", out var ff)) seed.FfmpegFolder = ff;
        var foreign = o.ContainsKey("foreign");   // simulate a settings file that came from another computer (paths that do not exist here)
        if (foreign) { seed.MediaFolder = @"Q:\Gone\Media"; seed.FfmpegFolder = @"Q:\gone\ffmpeg\bin"; }
        SettingsStore.Save(seed, path);
        if (foreign) File.WriteAllText(path, File.ReadAllText(path).Replace(Environment.MachineName, "OTHER-PC"));
        Console.WriteLine($"settings seeded: media='{seed.MediaFolder}' ffmpeg='{seed.FfmpegFolder}' foreign={foreign}");
        using var main = new MainForm(path, Path.Combine(outDir, "unused_cache.json"));
        main.StartPosition = FormStartPosition.Manual; main.Location = new Point(10, 10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        main.Show();
        var ui = 0;   // the UI thread must keep running while detection works
        while (main.CurrentSettings.Hardware == null && sw.Elapsed.TotalSeconds < 40) { Pump(100); ui++; }
        Console.WriteLine($"profile after {sw.Elapsed.TotalSeconds:0.0} s (UI pumped {ui} times)");
        Pump(300);
        var s = main.CurrentSettings;
        Console.WriteLine($"media field after start-up: '{s.MediaFolder}'  ffmpeg folder: '{s.FfmpegFolder}'");
        Console.WriteLine($"mode={s.Mode} preset={s.X265Preset} gpuCq={s.GpuCq} gpuJobs={s.ParallelGpuJobs} userModified=[{string.Join(",", s.UserModified)}]");
        Console.WriteLine("status line: " + FindStatus(main));
        main.Close();
        var saved = SettingsStore.Load(path);
        Console.WriteLine($"saved settings.json: hardware={(saved.Hardware == null ? "null" : saved.Hardware.Cpu.Name)} detectedUtc={saved.Hardware?.DetectedUtc:o}");
        // second launch: nothing changed, so no second detection
        var before = saved.Hardware!.DetectedUtc;
        using (var again = new MainForm(path, Path.Combine(outDir, "unused_cache.json")))
        {
            again.StartPosition = FormStartPosition.Manual; again.Location = new Point(10, 10);
            again.Show(); Pump(9000);
            Console.WriteLine("second launch re-detected: " + (again.CurrentSettings.Hardware!.DetectedUtc != before) + " ; status line: " + FindStatus(again));
            again.Close();
        }
        return 0;
    }

    private static string FindStatus(Form f) => string.Join(" | ", AllControls(f).OfType<Label>().Where(l => l.Text.Contains("Hardware detected") || l.Text.Contains("Detecting")).Select(l => l.Text));

    private static int HardwareRun(Dictionary<string, string> o, string outDir)
    {
        ApplicationConfiguration.Initialize();
        var loc = FfmpegLocator.Find(o.GetValueOrDefault("ffmpeg", ""));
        Console.WriteLine("ffmpeg: " + (loc?.FfmpegPath ?? "NOT FOUND"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hw = HardwareDetector.DetectAsync(loc).GetAwaiter().GetResult();
        Console.WriteLine($"detection took {sw.Elapsed.TotalSeconds:0.0} s");
        var policy = o.GetValueOrDefault("policy", "auto").ToLowerInvariant() switch { "cpu" => DefaultsPolicy.PreferCpuQuality, "gpu" => DefaultsPolicy.PreferGpuSpeed, _ => DefaultsPolicy.Auto };
        var s = new Settings { DefaultsPolicy = policy };
        var d = HardwareDefaults.Decide(hw, policy);
        var applied = HardwareDefaults.Apply(s, d, hw);
        Console.WriteLine("---- profile ----");
        Console.WriteLine(HardwareReport.Describe(hw));
        Console.WriteLine("applied: " + string.Join(", ", applied));
        Console.WriteLine($"mode={s.Mode} preset={s.X265Preset} cpuCrf={s.CpuCrf} gpuCq={s.GpuCq} cpuJobs={s.ParallelCpuJobs} gpuJobs={s.ParallelGpuJobs} x265Threads={s.X265Threads}");
        File.WriteAllText(Path.Combine(outDir, "hardware_profile.json"), JsonSerializer.Serialize(hw, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("saved " + Path.Combine(outDir, "hardware_profile.json"));

        var bench = o.GetValueOrDefault("bench", "");
        if (bench.Length > 0 && loc != null)
        {
            if (bench.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refusing: Z: is read-only for tests"); return 2; }
            var cache = ProbeCache.Load(Path.Combine(outDir, "bench_cache.json"));
            var res = new Scanner(new FfprobeRunner(loc.FfprobePath), cache, 2, false).ScanAsync(new[] { new LibraryRoot("B", Path.GetDirectoryName(Path.GetFullPath(bench))!) }, null, CancellationToken.None).GetAwaiter().GetResult();
            var e = res.Entries.FirstOrDefault(x => string.Equals(x.FullPath, Path.GetFullPath(bench), StringComparison.OrdinalIgnoreCase));
            if (e == null) { Console.WriteLine("bench file not found in scan"); return 2; }
            foreach (var mode in new[] { EncoderMode.Cpu, EncoderMode.Gpu })
            {
                var bs = SettingsStore.Clone(s); bs.Mode = mode;
                var plan = Planner.Plan(e, bs.ToPlannerOptions());
                if (plan.Action != PlanAction.ReencodeVideo) { Console.WriteLine($"{mode}: file is not a re-encode candidate ({plan.Action})"); continue; }
                var r = BenchmarkRunner.RunAsync(e, plan, bs, loc).GetAwaiter().GetResult();
                Console.WriteLine($"benchmark {mode}: ok={r.Ok} {r.Message}");
                if (r.Ok) { if (mode == EncoderMode.Cpu) s.BenchmarkCpuX = r.SpeedX; else s.BenchmarkGpuX = r.SpeedX; s.BenchmarkUtc = DateTime.UtcNow; }
            }
            var left = Directory.GetFiles(Path.GetTempPath(), "convertr_bench_*");
            Console.WriteLine("benchmark temp files left in %TEMP%: " + left.Length);
            var jobs = res.Entries.Select(x => (Plan: Planner.Plan(x, s.ToPlannerOptions()), Dur: x.Probe.DurationSec ?? 0)).Where(x => x.Plan.WritesFile).Select(x => (x.Plan, x.Dur)).ToList();
            Console.WriteLine($"estimate for {jobs.Count} file(s) in {s.Mode} mode: {SpeedEstimator.Format(SpeedEstimator.EstimateHours(jobs, s))}");
        }

        if (o.GetValueOrDefault("shots", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
            foreach (var (theme, suffix) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
            {
                var ss = SettingsStore.Clone(s); ss.Theme = theme;
                Theming.SetMode(theme);
                if (loc != null) ss.FfmpegFolder = loc.Folder;
                using var f = new SetupForm(ss);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(10, 10);
                f.Show(); Pump(3000);
                f.ClientSize = new Size(1050, 1180);
                Pump(300);
                Snap(f, Path.Combine(outDir, $"setup_hardware_{suffix}.png"));
                f.Close();
            }
        return 0;
    }
}
