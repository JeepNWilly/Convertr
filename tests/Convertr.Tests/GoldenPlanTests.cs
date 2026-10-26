// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Security.Cryptography;
using System.Text;
using Convertr.Core;
using Xunit;
using Xunit.Abstractions;

namespace Convertr.Tests;

/// <summary>
/// Refactor safety net: replays the real library scan through the planner, the savings model and the ffmpeg command
/// builder (CPU and GPU) and writes one line per file. Compare the output before and after a Core refactor:
/// set CONVERTR_GOLDEN_OUT to a file path to write the dump; the SHA-256 of the dump is printed.
/// Skipped when the scan CSV is not available.
/// </summary>
public class GoldenPlanTests
{
    private readonly ITestOutputHelper _out;
    public GoldenPlanTests(ITestOutputHelper output) { _out = output; }

    [SkippableFact]
    public void PlanSavingsAndCommands_Dump()
    {
        var env = Environment.GetEnvironmentVariable("CONVERTR_SCAN_CSV");
        var csv = new[] { env, "/workspace/library-scan/library_formats.csv", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LibraryScan", "library_formats.csv") }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
        Skip.If(csv == null, "library_formats.csv not found");
        var entries = RealScanCsvTests.ReadCsv(csv!).Select(RealScanCsvTests.ToEntry).ToList();
        var cpu = new Settings { Mode = EncoderMode.Cpu };
        var gpu = new Settings { Mode = EncoderMode.Gpu };
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            var plan = Planner.Plan(e, cpu.ToPlannerOptions());
            sb.Append(e.RelativePath).Append('|').Append(plan).Append('\n');
            foreach (var (name, s) in new[] { ("cpu", cpu), ("gpu", gpu) })
            {
                var o = s.ToPlannerOptions();
                var p = Planner.Plan(e, o);
                if (e.Probe.Video != null && e.Probe.Video.Kbps is long kb)
                    sb.Append("  est-").Append(name).Append(' ').Append(SavingsModel.Estimate(kb, e.Probe.Video.Width, e.Probe.Video.Height, e.Probe.DurationSec ?? 0, e.SizeBytes, o)).Append('\n');
                if (p.Action is PlanAction.ReencodeVideo or PlanAction.FixAudioOnly or PlanAction.Remux)
                {
                    var cmd = FfmpegCommandBuilder.Build(e, p, s, "X.part.mp4");
                    sb.Append("  cmd-").Append(name).Append(' ').Append(cmd.ToCommandLine()).Append(" | ").Append(cmd.Extension).Append('\n');
                }
            }
        }
        var text = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        _out.WriteLine($"golden dump: {entries.Count} files, {text.Length} chars, sha256 {hash}");
        var path = Environment.GetEnvironmentVariable("CONVERTR_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, text);
        Assert.True(text.Length > 1000);
    }
}
