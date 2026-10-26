// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Convertr.Core;

public sealed record ChapterEntry(double StartSec, double EndSec, string Title);

/// <summary>Chapter titles that are junk (URLs, ads, empty) are renamed "Chapter 01"...; the rest is kept. Written as an FFMETADATA file.</summary>
public static class ChapterFile
{
    /// <summary>Reads the "chapters" array of ffprobe JSON (start_time and end_time are seconds).</summary>
    public static List<ChapterEntry> ParseChapters(string json)
    {
        var list = new List<ChapterEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("chapters", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in arr.EnumerateArray())
        {
            double Num(string n) => c.TryGetProperty(n, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            var title = "";
            if (c.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var t in tags.EnumerateObject()) if (t.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = t.Value.GetString() ?? "";
            list.Add(new ChapterEntry(Num("start_time"), Num("end_time"), title));
        }
        return list;
    }

    public static string Build(IReadOnlyList<ChapterEntry> chapters, IReadOnlyList<string> junkPatterns)
    {
        var rx = MetadataAnalyzer.JunkRegex(junkPatterns);
        var sb = new StringBuilder(";FFMETADATA1\n");
        for (var i = 0; i < chapters.Count; i++)
        {
            var c = chapters[i];
            var title = string.IsNullOrWhiteSpace(c.Title) || rx.IsMatch(c.Title) ? "Chapter " + (i + 1).ToString("00", CultureInfo.InvariantCulture) : c.Title;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(((long)Math.Round(c.StartSec * 1000)).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("END=").Append(((long)Math.Round(c.EndSec * 1000)).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("title=").Append(Escape(title)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\r", "").Replace("\n", "\\\n");
}
