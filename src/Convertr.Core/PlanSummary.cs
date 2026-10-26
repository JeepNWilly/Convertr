// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text;

namespace Convertr.Core;

/// <summary>Counts and totals for the summary bar. All savings are estimates.</summary>
public sealed class PlanSummary
{
    public int Total { get; init; }
    public long TotalBytes { get; init; }
    public int Skip { get; init; }
    public int Remux { get; init; }
    public int ReencodeSize { get; init; }
    public int ReencodeCompat { get; init; }
    public int FixAudio { get; init; }
    public int Error { get; init; }
    public int Review { get; init; }
    /// <summary>Files (in re-encode or audio-fix actions) that also convert audio.</summary>
    public int AudioConversions { get; init; }
    public int Warnings { get; init; }
    public long SizeCandidateBytes { get; init; }
    public long EstSaveLowBytes { get; init; }
    public long EstSaveHighBytes { get; init; }
    /// <summary>Expected total saving (files that would probably be refused count as 0).</summary>
    public long EstSaveMidBytes { get; init; }
    /// <summary>Size candidates the model expects to fail the keep-if-smaller rule (the encode would be thrown away) and their size.</summary>
    public int LikelyRefused { get; init; }
    public long LikelyRefusedBytes { get; init; }

    public int ReencodeTotal => ReencodeSize + ReencodeCompat;

    public static PlanSummary From(IEnumerable<(ScanEntry Entry, PlanItem Plan)> rows)
    {
        int total = 0, skip = 0, remux = 0, size = 0, compat = 0, fix = 0, err = 0, rev = 0, aud = 0, warn = 0;
        long bytes = 0, cand = 0, lo = 0, hi = 0, mid = 0, refBytes = 0;
        var refused = 0;
        foreach (var (e, p) in rows)
        {
            total++;
            bytes += e.SizeBytes;
            if (p.HasWarnings) warn++;
            switch (p.Action)
            {
                case PlanAction.Skip: skip++; break;
                case PlanAction.Remux: remux++; break;
                case PlanAction.Error: err++; break;
                case PlanAction.Review: rev++; break;
                case PlanAction.FixAudioOnly: fix++; aud++; break;
                case PlanAction.ReencodeVideo:
                    if (p.Kind == ReencodeKind.Compat) compat++; else { size++; cand += e.SizeBytes; }
                    if (p.AudioConversion) aud++;
                    lo += p.EstSaveLowBytes;
                    hi += p.EstSaveHighBytes;
                    mid += p.EstSaveMidBytes;
                    if (p.Kind != ReencodeKind.Compat && p.LikelyRefused) { refused++; refBytes += e.SizeBytes; }
                    break;
            }
        }
        return new PlanSummary
        {
            Total = total, TotalBytes = bytes, Skip = skip, Remux = remux, ReencodeSize = size, ReencodeCompat = compat,
            FixAudio = fix, Error = err, Review = rev, AudioConversions = aud, Warnings = warn,
            SizeCandidateBytes = cand, EstSaveLowBytes = lo, EstSaveHighBytes = hi, EstSaveMidBytes = mid, LikelyRefused = refused, LikelyRefusedBytes = refBytes,
        };
    }

    public static string Gb(long bytes) => (bytes / 1073741824.0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
}

/// <summary>Writes the plan as CSV (UTF-8 with BOM, CRLF), one row per file.</summary>
public static class PlanCsv
{
    public static readonly string[] Columns =
    {
        "Library", "RelativePath", "Container", "VideoCodec", "Width", "Height", "Resolution", "BitDepth", "VideoKbps", "KbpsSource",
        "Audio", "Subtitles", "SizeMB", "Action", "Kind", "Rule", "Reason", "EstSaveLowMB", "EstSaveHighMB", "AudioConversion",
        "StripsSubs", "Metadata", "Warnings",
    };

    public static string Describe(ProbeInfo p) => string.Join("; ", p.Audio.Select(a => a.Language + " " + a.Describe()));

    public static string DescribeSubs(ProbeInfo p) => string.Join("; ", p.Subs.Select(s => s.Language + " " + s.Codec + (s.IsImage ? " [IMAGE]" : "")));

    public static string Build(IEnumerable<(ScanEntry Entry, PlanItem Plan)> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns.Select(Q))).Append("\r\n");
        foreach (var (e, pl) in rows)
        {
            var p = e.Probe;
            var v = p.Video;
            var cells = new[]
            {
                e.Library, e.RelativePath, p.FormatName ?? "", v?.Codec ?? "", v?.Width.ToString() ?? "", v?.Height.ToString() ?? "",
                v == null ? "" : ResolutionClassifier.Label(pl.Resolution), v?.BitDepth.ToString() ?? "", v?.Kbps?.ToString() ?? "", v?.KbpsSource ?? "",
                Describe(p), DescribeSubs(p), (e.SizeBytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                pl.ActionLabel, pl.Kind.ToString(), pl.Rule.ToString(), pl.Reason,
                (pl.EstSaveLowBytes / 1048576).ToString(), (pl.EstSaveHighBytes / 1048576).ToString(),
                pl.AudioConversion ? "yes" : "", pl.StripsSubs && pl.WritesFile ? "yes" : "", pl.Metadata.Summary, p.Warnings ?? "",
            };
            sb.Append(string.Join(",", cells.Select(Q))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<(ScanEntry Entry, PlanItem Plan)> rows)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Build(rows), new UTF8Encoding(true));
    }

    private static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
}
