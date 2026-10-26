// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Convertr.Core;

/// <summary>What "Clean up junk metadata" would remove from one file (DESIGN.md 3.6). Findings only, nothing is changed.</summary>
public sealed record MetadataFindings
{
    public int JunkGlobalTags { get; init; }
    public int JunkStreamTags { get; init; }
    public bool StatsTags { get; init; }
    public int Attachments { get; init; }
    public int CoverArt { get; init; }
    public int DataStreams { get; init; }
    public int JunkChapters { get; init; }
    public bool TitleCleared { get; init; }

    public bool IsClean =>
        JunkGlobalTags == 0 && JunkStreamTags == 0 && !StatsTags && Attachments == 0 && CoverArt == 0 &&
        DataStreams == 0 && JunkChapters == 0 && !TitleCleared;

    public string Summary
    {
        get
        {
            if (IsClean) return "clean";
            var parts = new List<string>();
            var tags = JunkGlobalTags + JunkStreamTags;
            if (tags > 0) parts.Add(tags + (tags == 1 ? " tag" : " tags"));
            if (TitleCleared) parts.Add("title");
            if (StatsTags) parts.Add("stats tags");
            if (Attachments > 0) parts.Add(Attachments + (Attachments == 1 ? " attachment" : " attachments"));
            if (CoverArt > 0) parts.Add(CoverArt + " cover art");
            if (DataStreams > 0) parts.Add(DataStreams + " data");
            if (JunkChapters > 0) parts.Add(JunkChapters + (JunkChapters == 1 ? " chapter title" : " chapter titles"));
            return "junk: " + string.Join(", ", parts);
        }
    }
}

public static class MetadataAnalyzer
{
    private static readonly HashSet<string> JunkKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "comment", "description", "synopsis", "encoder", "encoded_by", "encoding_tool", "url", "purl",
        "copyright", "publisher", "website", "source", "contact",
    };

    private static readonly HashSet<string> BenignKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "creation_time", "major_brand", "minor_version", "compatible_brands", "language", "duration",
        "handler_name", "vendor_id",
    };

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public static Regex JunkRegex(IReadOnlyList<string> patterns)
    {
        var key = string.Join("\u0001", patterns);
        return RegexCache.GetOrAdd(key, k =>
        {
            var valid = new List<string>();
            foreach (var p in patterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try { _ = new Regex(p); valid.Add("(?:" + p + ")"); } catch (ArgumentException) { /* ignore bad pattern */ }
            }
            if (valid.Count == 0) return new Regex("(?!)");
            return new Regex(string.Join("|", valid), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });
    }

    public static MetadataFindings Analyze(ScanEntry e, PlannerOptions o)
    {
        var p = e.Probe;
        var rx = JunkRegex(o.JunkPatterns);

        var global = 0;
        var titleCleared = false;
        foreach (var kv in p.FormatTags)
        {
            if (kv.Key.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(kv.Value) && (!o.KeepSensibleTitle || !IsSensibleTitle(kv.Value, e.BaseName, rx)))
                    titleCleared = true;
                continue;
            }
            if (JunkKeys.Contains(kv.Key)) { global++; continue; }
            if (!BenignKeys.Contains(kv.Key) && rx.IsMatch(kv.Value)) global++;
        }

        var streamJunk = 0;
        foreach (var t in StreamTexts(p)) if (rx.IsMatch(t)) streamJunk++;

        var chapters = 0;
        foreach (var t in p.ChapterTitles) if (string.IsNullOrWhiteSpace(t) || rx.IsMatch(t)) chapters++;

        return new MetadataFindings
        {
            JunkGlobalTags = global,
            JunkStreamTags = streamJunk,
            StatsTags = p.StatsTagStreams > 0,
            Attachments = p.AttachmentCount,
            CoverArt = p.AttachedPicCount,
            DataStreams = p.DataStreamCount,
            JunkChapters = chapters,
            TitleCleared = titleCleared,
        };
    }

    private static IEnumerable<string> StreamTexts(ProbeInfo p)
    {
        if (p.Video != null)
        {
            if (!string.IsNullOrEmpty(p.Video.Title)) yield return p.Video.Title!;
            if (!string.IsNullOrEmpty(p.Video.HandlerName)) yield return p.Video.HandlerName!;
        }
        foreach (var a in p.Audio)
        {
            if (!string.IsNullOrEmpty(a.Title)) yield return a.Title!;
            if (!string.IsNullOrEmpty(a.HandlerName)) yield return a.HandlerName!;
        }
        foreach (var s in p.Subs)
        {
            if (!string.IsNullOrEmpty(s.Title)) yield return s.Title!;
            if (!string.IsNullOrEmpty(s.HandlerName)) yield return s.HandlerName!;
        }
    }

    /// <summary>A title is sensible when it matches the file's base name or "Title (Year)" and contains no junk.</summary>
    public static bool IsSensibleTitle(string title, string baseName, Regex junk)
    {
        if (junk.IsMatch(title)) return false;
        var t = Norm(title);
        var b = Norm(baseName);
        if (t.Length == 0) return false;
        if (t == b) return true;
        if (b.StartsWith(t, StringComparison.Ordinal)) return true;
        return t.Length >= 4 && b.Contains(t, StringComparison.Ordinal);
    }

    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
