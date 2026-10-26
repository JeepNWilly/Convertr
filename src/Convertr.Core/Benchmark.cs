// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Globalization;

namespace Convertr.Core;

public sealed record BenchmarkResult(bool Ok, double SpeedX, double ClipSeconds, double ElapsedSeconds, string Message);

/// <summary>
/// The optional 20-second benchmark: encodes 20 s of a candidate file with the exact command Convertr would use (current mode and
/// settings) into a scratch file in %TEMP%, deletes it, and reports the speed as times real time. The source is only read.
/// </summary>
public static class BenchmarkRunner
{
    /// <summary>The ffmpeg arguments: the real command with input options "-ss START -t SECONDS" added before the input.</summary>
    public static List<string> BuildArgs(ScanEntry e, PlanItem plan, Settings s, string tempOut, double start, double seconds)
    {
        var cmd = FfmpegCommandBuilder.Build(e, plan, s, tempOut);
        var args = new List<string>(cmd.Args);
        var i = args.IndexOf("-i");
        args.InsertRange(i, new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture) });
        return args;
    }

    public static async Task<BenchmarkResult> RunAsync(ScanEntry e, PlanItem plan, Settings s, FfmpegLocation loc, CancellationToken ct = default, string? tempDir = null)
    {
        if (string.IsNullOrEmpty(loc.FfmpegPath)) return new(false, 0, 0, 0, "ffmpeg.exe was not found.");
        var dur = e.Probe.DurationSec ?? 0;
        if (dur < 30) return new(false, 0, 0, 0, "The sample file is too short for a benchmark.");
        var seconds = 20.0;
        var start = dur > 180 ? Math.Min(dur / 3, dur - seconds - 5) : 0;
        var dir = tempDir ?? Path.GetTempPath();
        var tmp = Path.Combine(dir, "convertr_bench_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        try
        {
            var args = BuildArgs(e, plan, s, tmp, start, seconds);
            var psi = new ProcessStartInfo { FileName = loc.FfmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            FfmpegEnvironment.Apply(psi);
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            _ = p.StandardOutput.ReadToEndAsync(ct);
            var err = p.StandardError.ReadToEndAsync(ct);
            using var killer = ct.Register(() => { try { p.Kill(true); } catch { /* gone */ } });
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            sw.Stop();
            if (ct.IsCancellationRequested) return new(false, 0, seconds, sw.Elapsed.TotalSeconds, "Cancelled.");
            if (p.ExitCode != 0)
            {
                var text = (await err.ConfigureAwait(false)).Trim();
                var msg = s.Mode == EncoderMode.Gpu ? FfmpegLocator.NvencProblem(text) : (text.Split('\n').FirstOrDefault() ?? "ffmpeg failed");
                return new(false, 0, seconds, sw.Elapsed.TotalSeconds, "The benchmark encode failed: " + msg);
            }
            var x = seconds / Math.Max(0.1, sw.Elapsed.TotalSeconds);
            return new(true, x, seconds, sw.Elapsed.TotalSeconds, $"{x:0.0}x real time ({seconds:0} s of video in {sw.Elapsed.TotalSeconds:0.0} s)");
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* scratch file in TEMP */ } }
    }
}
