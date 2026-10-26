// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text.RegularExpressions;
using Convertr.Core;
using Xunit;
using Xunit.Abstractions;

namespace Convertr.Tests;

/// <summary>
/// Replays the real library scan (library_formats.csv, made by the earlier read-only scan) through the planner.
/// Skipped when the CSV is not available. Set CONVERTR_SCAN_CSV to point at it.
/// </summary>
public class RealScanCsvTests
{
    private readonly ITestOutputHelper _out;
    public RealScanCsvTests(ITestOutputHelper output) { _out = output; }

    private static string? FindCsv()
    {
        var env = Environment.GetEnvironmentVariable("CONVERTR_SCAN_CSV");
        foreach (var c in new[] { env, "/workspace/library-scan/library_formats.csv", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LibraryScan", "library_formats.csv") })
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        return null;
    }

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var text = File.ReadAllText(path);
        var rows = new List<List<string>>();
        var row = new List<string>(); var cell = new System.Text.StringBuilder(); var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                else cell.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        var head = rows[0].Select(h => h.TrimStart('\uFEFF')).ToList();
        return rows.Skip(1).Select(r => head.Select((h, i) => (h, v: i < r.Count ? r[i] : "")).ToDictionary(x => x.h, x => x.v)).ToList();
    }

    private static readonly Regex AudioRx = new(@"^(\S+) ([a-z0-9_]+)(\(.*\))? (\d+)ch$", RegexOptions.Compiled);

    public static ScanEntry ToEntry(Dictionary<string, string> r)
    {
        var info = new ProbeInfo { FormatName = r["Container"], Warnings = null };
        var err = r["Error"];
        if (r["VideoCodec"] == "" && r["Container"] == "") info.ProbeError = err;
        if (double.TryParse(r["DurationMin"], NumberStyles.Float, CultureInfo.InvariantCulture, out var dur)) info.DurationSec = dur * 60;
        if (r["VideoCodec"] != "")
        {
            info.Video = new VideoInfo
            {
                Codec = r["VideoCodec"],
                Width = int.Parse(r["Width"]),
                Height = int.Parse(r["Height"]),
                BitDepth = int.TryParse(r["BitDepth"], out var bd) ? bd : 0,
                Hdr = r["HDR"],
                Kbps = long.TryParse(r["VideoBitrateKbps"], out var kb) ? kb : null,
                KbpsSource = r["VBRSource"],
            };
        }
        foreach (var a in r["AudioSummary"].Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var m = AudioRx.Match(a);
            Assert.True(m.Success, "unparsed audio summary: " + a);
            info.Audio.Add(new AudioInfo { Language = m.Groups[1].Value, Codec = m.Groups[2].Value, Channels = int.Parse(m.Groups[4].Value) });
        }
        foreach (var s in r["SubSummary"].Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = s.Split(' ');
            info.Subs.Add(new SubInfo { Language = parts[0], Codec = parts.Length > 1 ? parts[1] : "", IsImage = s.Contains("[IMAGE]") });
        }
        return new ScanEntry
        {
            Library = r["Library"],
            RelativePath = r["RelativePath"],
            FullPath = r["RelativePath"],
            SizeBytes = (long)(double.Parse(r["SizeMB"], CultureInfo.InvariantCulture) * 1048576),
            HasExternalSubs = r["ExternalSubs"] != "0" && r["ExternalSubs"] != "",
            Probe = info,
        };
    }

    [SkippableFact]
    public void PlanCounts_MatchTheDesign_AtDefaultThresholds()
    {
        var csv = FindCsv();
        Skip.If(csv == null, "library_formats.csv not found (set CONVERTR_SCAN_CSV)");
        var rows = ReadCsv(csv!);
        Assert.Equal(3140, rows.Count);

        var entries = rows.Select(ToEntry).ToList();
        var opts = new PlannerOptions();
        var plans = entries.Select(e => (Entry: e, Plan: Planner.Plan(e, opts))).ToList();
        var s = PlanSummary.From(plans);
        _out.WriteLine($"files={s.Total} compat={s.ReencodeCompat} size={s.ReencodeSize} fixAudio={s.FixAudio} remux={s.Remux} skip={s.Skip} error={s.Error} review={s.Review} audioConversions={s.AudioConversions} sizeCandidateGB={s.SizeCandidateBytes / 1073741824.0:N0}");

        Assert.Equal(249, s.ReencodeCompat);                 // design: 249 legacy re-encodes
        Assert.Equal(828, s.ReencodeSize);                   // design: 828 H.264 -> HEVC candidates
        Assert.Equal(1, s.Error);                            // design: 1 error (Movie_C)
        Assert.Equal(0, s.Remux);
        Assert.Equal(0, s.Review);
        // The design's "35 audio fixes" are the 35 DTS files. Rule order (5 before 6) puts 31 of them in the size
        // re-encode group (their audio is converted in the same pass); only 4 are audio-only fixes.
        Assert.Equal(4, s.FixAudio);
        Assert.Equal(35, plans.Count(x => x.Plan.Action is PlanAction.FixAudioOnly or PlanAction.ReencodeVideo && x.Plan.Kind != ReencodeKind.Compat && x.Plan.AudioConversion));
        Assert.Equal(3140 - 249 - 828 - 4 - 1, s.Skip);
        Assert.EndsWith(".mp4", plans.Single(x => x.Plan.Action == PlanAction.Error).Entry.FileName);   // the one corrupt MP4 in the scan
    }

    [SkippableFact]
    public void ResolutionClassifier_AgreesWithEveryRowOfTheScan()
    {
        var csv = FindCsv();
        Skip.If(csv == null, "library_formats.csv not found");
        foreach (var r in ReadCsv(csv!).Where(r => r["Width"] != ""))
            Assert.Equal(r["Resolution"], ResolutionClassifier.Label(ResolutionClassifier.Classify(int.Parse(r["Width"]), int.Parse(r["Height"]))));
    }

    [SkippableFact]
    public void SizeCandidates_SplitByResolutionAsInTheDesign()
    {
        var csv = FindCsv();
        Skip.If(csv == null, "library_formats.csv not found");
        var plans = ReadCsv(csv!).Select(ToEntry).Select(e => (Entry: e, Plan: Planner.Plan(e, new PlannerOptions()))).ToList();
        var size = plans.Where(x => x.Plan.Kind == ReencodeKind.Size).ToList();
        Assert.Equal(189, size.Count(x => x.Plan.Resolution == ResolutionClass.P1080));   // design table 3.3
        Assert.Equal(623, size.Count(x => x.Plan.Resolution == ResolutionClass.P720));
        Assert.Equal(16, size.Count(x => x.Plan.Resolution is ResolutionClass.P480 or ResolutionClass.P576));
        var gb = size.Sum(x => x.Entry.SizeBytes) / 1073741824.0;
        _out.WriteLine($"size candidates: {size.Count} files, {gb:N0} GB");
        Assert.InRange(gb, 1200, 1260);                                                      // design: 1,238 GB
    }

    [SkippableFact]
    public void LegacyFiles_AreExactlyTheAviWmvMpgFiles()
    {
        var csv = FindCsv();
        Skip.If(csv == null, "library_formats.csv not found");
        var rows = ReadCsv(csv!);
        var legacy = rows.Where(r => Planner.Plan(ToEntry(r), new PlannerOptions()).Kind == ReencodeKind.Compat).ToList();
        Assert.Equal(249, legacy.Count);
        Assert.All(legacy, r => Assert.Contains(r["Container"], new[] { "avi", "asf", "mpeg" }));
    }
}
