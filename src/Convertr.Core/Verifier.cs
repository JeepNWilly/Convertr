// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;

namespace Convertr.Core;

public enum VerifyVerdict { Pass, NotWorthIt, Fail }

public sealed record VerifyResult(VerifyVerdict Verdict, IReadOnlyList<string> Failures, IReadOnlyList<string> Notes, double SavingPercent, long SavedBytes)
{
    public bool Passed => Verdict == VerifyVerdict.Pass;
    public string Summary => Verdict switch
    {
        VerifyVerdict.Pass => "verified: " + string.Join("; ", Notes),
        VerifyVerdict.NotWorthIt => "not worth it: " + string.Join("; ", Notes),
        _ => "verification FAILED: " + string.Join("; ", Failures),
    };
}

/// <summary>
/// The checks from DESIGN.md section 5.3, as a pure function over the source facts, the expected output and the ffprobe result
/// of the temporary output. The decode test (which needs ffmpeg) is done separately by the engine.
/// </summary>
public static class Verifier
{
    private static readonly HashSet<string> BenignTagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "major_brand", "minor_version", "compatible_brands", "title", "language", "handler_name", "duration", "vendor_id",
        "creation_time", "encoder",
    };

    public static VerifyResult Verify(ScanEntry source, PlanItem plan, ExpectedOutput expected, ProbeInfo output, long outputBytes, Settings s)
    {
        var fail = new List<string>();
        var notes = new List<string>();
        var sp = source.Probe;

        if (output.ProbeError != null)
            return new VerifyResult(VerifyVerdict.Fail, new[] { "the output could not be read by ffprobe: " + output.ProbeError }, notes, 0, 0);

        // 1. duration
        if (sp.DurationSec is double sd && output.DurationSec is double od)
        {
            var tol = Math.Max(1.0, sd * 0.005);
            if (Math.Abs(sd - od) > tol)
                fail.Add($"duration differs: source {sd.ToString("0.0", CultureInfo.InvariantCulture)} s, output {od.ToString("0.0", CultureInfo.InvariantCulture)} s (allowed {tol.ToString("0.0", CultureInfo.InvariantCulture)} s)");
            else notes.Add("duration " + od.ToString("0", CultureInfo.InvariantCulture) + " s");
        }
        else fail.Add("duration missing in the output");

        // 2. video
        var v = output.Video;
        var sv = sp.Video;
        if (v == null) fail.Add("no video stream in the output");
        else
        {
            if (output.ExtraVideoStreams > 0) fail.Add("more than one video stream in the output");
            if (!v.Codec.Equals(expected.VideoCodec, StringComparison.OrdinalIgnoreCase))
                fail.Add($"video codec is {v.Codec}, expected {expected.VideoCodec}");
            if (sv != null && (v.Width != sv.Width || v.Height != sv.Height))
                fail.Add($"resolution changed: {sv.Width}x{sv.Height} became {v.Width}x{v.Height}");
        }

        // 3. audio
        if (output.Audio.Count != expected.AudioCodecs.Count)
            fail.Add($"{output.Audio.Count} audio streams in the output, expected {expected.AudioCodecs.Count}");
        else
            for (var i = 0; i < output.Audio.Count; i++)
            {
                if (!output.Audio[i].Codec.Equals(expected.AudioCodecs[i], StringComparison.OrdinalIgnoreCase))
                    fail.Add($"audio {i + 1} is {output.Audio[i].Codec}, expected {expected.AudioCodecs[i]}");
                if (expected.Cleaned && !string.Equals(output.Audio[i].Language, expected.AudioLanguages[i], StringComparison.OrdinalIgnoreCase))
                    fail.Add($"audio {i + 1} language is {output.Audio[i].Language}, expected {expected.AudioLanguages[i]}");
            }

        // 4. subtitles / attachments / data
        if (output.Subs.Count != expected.SubtitleStreams) fail.Add($"{output.Subs.Count} subtitle streams in the output, expected {expected.SubtitleStreams}");
        if (output.AttachmentCount > 0 || output.AttachedPicCount > 0 || output.DataStreamCount > 0)
            fail.Add("the output contains attachments, cover art or data streams");

        // 5. chapters
        if (expected.ChaptersDropped && output.ChapterCount > 0) fail.Add("chapters were supposed to be dropped");
        if (!expected.ChaptersDropped && sp.ChapterCount > 0 && output.ChapterCount != sp.ChapterCount)
            notes.Add($"warning: chapters: source had {sp.ChapterCount}, output has {output.ChapterCount}");

        // 6. cleaned metadata
        if (expected.Cleaned)
        {
            var rx = MetadataAnalyzer.JunkRegex(s.JunkPatterns);
            foreach (var kv in output.FormatTags)
            {
                if (!BenignTagKeys.Contains(kv.Key)) { fail.Add($"unexpected format tag '{kv.Key}' in the output"); continue; }
                if (kv.Key.Equals("encoder", StringComparison.OrdinalIgnoreCase) && !kv.Value.StartsWith("Lavf", StringComparison.OrdinalIgnoreCase))
                    fail.Add("unexpected encoder tag in the output");
                else if (!kv.Key.Equals("encoder", StringComparison.OrdinalIgnoreCase) && rx.IsMatch(kv.Value))
                    fail.Add($"junk text in format tag '{kv.Key}'");
            }
            if (output.StatsTagStreams > 0) fail.Add("mkvmerge statistics tags are still present");
            if (v != null && ((v.Title != null && rx.IsMatch(v.Title)) || (v.HandlerName != null && rx.IsMatch(v.HandlerName)))) fail.Add("junk stream title in the output");
            foreach (var a in output.Audio) if ((a.Title != null && rx.IsMatch(a.Title)) || (a.HandlerName != null && rx.IsMatch(a.HandlerName))) fail.Add("junk audio title in the output");
        }

        // 7. size sanity
        var src = (double)Math.Max(1, source.SizeBytes);
        var ratio = outputBytes / src;
        if (outputBytes <= 0) fail.Add("the output is empty");
        else if (ratio < 0.05) fail.Add($"the output is only {ratio:P1} of the source (looks truncated)");
        var max = plan.Action == PlanAction.ReencodeVideo ? (plan.Kind == ReencodeKind.Compat ? 1.5 : 1.05) : 1.10;
        if (ratio > max) fail.Add($"the output is {ratio:P0} of the source (more than {max:P0} allowed)");

        var saved = source.SizeBytes - outputBytes;
        var pct = (1 - ratio) * 100;
        if (fail.Count > 0) return new VerifyResult(VerifyVerdict.Fail, fail, notes, pct, saved);

        notes.Add($"size {ratio:P0} of the source ({pct.ToString("0.0", CultureInfo.InvariantCulture)}% saved)");

        // 8. keep-only-if-smaller (size re-encodes only)
        if (plan.Action == PlanAction.ReencodeVideo && plan.Kind == ReencodeKind.Size)
        {
            var needBytes = (long)s.KeepIfSmallerMb * 1048576L;
            if (pct < s.KeepIfSmallerPercent || saved < needBytes)
            {
                notes.Add($"saving {pct.ToString("0.0", CultureInfo.InvariantCulture)}% / {saved / 1048576} MB is below the {s.KeepIfSmallerPercent}% and {s.KeepIfSmallerMb} MB limit");
                return new VerifyResult(VerifyVerdict.NotWorthIt, fail, notes, pct, saved);
            }
        }
        return new VerifyResult(VerifyVerdict.Pass, fail, notes, pct, saved);
    }
}
