// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertr.Core;

/// <summary>
/// Turns ffprobe JSON (-show_format -show_streams -show_chapters) into a <see cref="ProbeInfo"/>.
/// Pure: no disk or process access. The bitrate logic mirrors the library scan so the numbers match.
/// </summary>
public static class ProbeParser
{
    private static readonly HashSet<string> ImageSubCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub" };

    public static ProbeInfo Failed(string message) => new() { ProbeError = message };

    public static ProbeInfo Parse(string? json, string? stderr, int exitCode = 0)
    {
        var warnings = CleanStderr(stderr);
        if (string.IsNullOrWhiteSpace(json))
        {
            var why = string.IsNullOrEmpty(warnings) ? $"ffprobe returned no output (exit code {exitCode})" : warnings;
            return Failed("ffprobe: " + why);
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return Failed("ffprobe output was not valid JSON: " + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            var info = new ProbeInfo();
            JsonElement fmt = default;
            var hasFormat = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format", out fmt) && fmt.ValueKind == JsonValueKind.Object;
            if (!hasFormat && !(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("streams", out _)))
            {
                var why = string.IsNullOrEmpty(warnings) ? "no format or streams found" : warnings;
                return Failed("ffprobe: " + why);
            }

            if (hasFormat)
            {
                info.FormatName = Str(fmt, "format_name");
                info.DurationSec = Dbl(fmt, "duration");
                var br = Dbl(fmt, "bit_rate");
                if (br.HasValue) info.OverallKbps = (long)Math.Round(br.Value / 1000.0);
                if (fmt.TryGetProperty("tags", out var ft) && ft.ValueKind == JsonValueKind.Object)
                    foreach (var p in ft.EnumerateObject())
                        info.FormatTags[p.Name] = Trunc(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText(), 300) ?? "";
            }

            var audioBps = new List<double>();
            VideoInfo? video = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type") ?? "";
                    var codec = Str(s, "codec_name") ?? "";
                    var attachedPic = DispositionFlag(s, "attached_pic");
                    HasStatsTag(s, info);
                    switch (type)
                    {
                        case "video":
                            if (attachedPic) { info.AttachedPicCount++; break; }
                            if (video != null) { info.ExtraVideoStreams++; break; }
                            video = ParseVideo(s, codec);
                            break;
                        case "audio":
                        {
                            var a = new AudioInfo
                            {
                                Codec = codec,
                                Profile = Str(s, "profile"),
                                Channels = (int)(Dbl(s, "channels") ?? 0),
                                ChannelLayout = Str(s, "channel_layout"),
                                Language = Tag(s, "language") ?? "und",
                                Title = Trunc(Tag(s, "title"), 200),
                                HandlerName = Trunc(Tag(s, "handler_name"), 200),
                                IsDefault = DispositionFlag(s, "default"),
                            };
                            var sbr = Dbl(s, "bit_rate");
                            var tbr = Dbl(Tag(s, "BPS"));
                            if (sbr.HasValue) { a.Kbps = (long)Math.Round(sbr.Value / 1000.0); audioBps.Add(sbr.Value); }
                            else if (tbr.HasValue) { a.Kbps = (long)Math.Round(tbr.Value / 1000.0); audioBps.Add(tbr.Value); }
                            else audioBps.Add(128000);
                            info.Audio.Add(a);
                            break;
                        }
                        case "subtitle":
                            info.Subs.Add(new SubInfo
                            {
                                Codec = codec,
                                Language = Tag(s, "language") ?? "und",
                                Title = Trunc(Tag(s, "title"), 200),
                                HandlerName = Trunc(Tag(s, "handler_name"), 200),
                                IsImage = ImageSubCodecs.Contains(codec),
                                Forced = DispositionFlag(s, "forced"),
                            });
                            break;
                        case "attachment": info.AttachmentCount++; break;
                        case "data": info.DataStreamCount++; break;
                    }
                }
            }

            if (video != null)
            {
                // Video bitrate: stream value, else BPS tag, else overall bitrate minus audio (128 kbps assumed when unknown).
                // A stream or BPS-tag value that is larger than the bitrate of the whole file is stale (left over from the original rip
                // after the file was trimmed or re-muxed; 166 files in the library, for example Series_Q S01E06: tag 7,012 kbps, real 912 kbps).
                // It is ignored and the bitrate is estimated from the file size instead.
                if (video.Kbps != null && video.KbpsSource is "stream" or "tag" && info.OverallKbps is long ok && ok > 0 && video.Kbps > ok * 1.10 + 100)
                {
                    video.Kbps = null;
                    video.KbpsSource = "";
                }
                if (video.Kbps == null && video.KbpsSource == "" && info.OverallKbps.HasValue && fmtBitRate(fmt, hasFormat) is double overall)
                {
                    var ab = audioBps.Sum();
                    video.Kbps = (long)Math.Max(0, Math.Round((overall - ab) / 1000.0));
                    video.KbpsSource = "estimated";
                }
                info.Video = video;
            }

            if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chapters.EnumerateArray())
                {
                    info.ChapterCount++;
                    string? t = null;
                    if (c.TryGetProperty("tags", out var ct) && ct.ValueKind == JsonValueKind.Object)
                        foreach (var p in ct.EnumerateObject())
                            if (p.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) t = p.Value.GetString();
                    info.ChapterTitles.Add(Trunc(t, 120) ?? "");
                }
            }

            info.Warnings = warnings;
            if (info.Video == null && info.Audio.Count == 0 && info.Subs.Count == 0 && !hasFormat)
                return Failed("ffprobe: " + (string.IsNullOrEmpty(warnings) ? "no streams found" : warnings));
            return info;
        }
    }

    private static double? fmtBitRate(JsonElement fmt, bool has) => has ? Dbl(fmt, "bit_rate") : null;

    private static VideoInfo ParseVideo(JsonElement s, string codec)
    {
        var v = new VideoInfo
        {
            Codec = codec,
            Profile = Str(s, "profile"),
            Width = (int)(Dbl(s, "width") ?? 0),
            Height = (int)(Dbl(s, "height") ?? 0),
            PixFmt = Str(s, "pix_fmt"),
            FieldOrder = Str(s, "field_order"),
            ColorTransfer = Str(s, "color_transfer"),
            Title = Trunc(Tag(s, "title"), 200),
            HandlerName = Trunc(Tag(s, "handler_name"), 200),
        };
        var fps = Str(s, "avg_frame_rate");
        if (string.IsNullOrEmpty(fps) || fps == "0/0") fps = Str(s, "r_frame_rate");
        v.Fps = ParseRate(fps);

        var bd = Str(s, "bits_per_raw_sample");
        if (string.IsNullOrEmpty(bd) && v.PixFmt != null)
        {
            var m = Regex.Match(v.PixFmt, @"(\d\d)(le|be)");
            if (m.Success) bd = m.Groups[1].Value;
        }
        if (string.IsNullOrEmpty(bd) && !string.IsNullOrEmpty(v.PixFmt)) bd = "8";
        if (int.TryParse(bd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)) v.BitDepth = bits;

        var hdr = new List<string>();
        if (v.ColorTransfer == "smpte2084") hdr.Add("HDR10");
        if (v.ColorTransfer == "arib-std-b67") hdr.Add("HLG");
        if (s.TryGetProperty("side_data_list", out var sd) && sd.ValueKind == JsonValueKind.Array)
            foreach (var d in sd.EnumerateArray())
            {
                var t = Str(d, "side_data_type") ?? "";
                if (t.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || t.Contains("Dolby", StringComparison.OrdinalIgnoreCase))
                { hdr.Add("DolbyVision"); break; }
            }
        v.Hdr = string.Join("+", hdr);

        var sbr = Dbl(s, "bit_rate");
        var tbr = Dbl(Tag(s, "BPS"));
        if (sbr.HasValue) { v.Kbps = (long)Math.Round(sbr.Value / 1000.0); v.KbpsSource = "stream"; }
        else if (tbr.HasValue) { v.Kbps = (long)Math.Round(tbr.Value / 1000.0); v.KbpsSource = "tag"; }
        return v;
    }

    private static double? ParseRate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return null;
        var m = Regex.Match(r, @"^(\d+)/(\d+)$");
        if (m.Success)
        {
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var d = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return d == 0 ? null : Math.Round(n / d, 2);
        }
        return Dbl(r);
    }

    private static void HasStatsTag(JsonElement s, ProbeInfo info)
    {
        if (s.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject())
                if (p.Name.StartsWith("_STATISTICS", StringComparison.OrdinalIgnoreCase)) { info.StatsTagStreams++; return; }
    }

    private static bool DispositionFlag(JsonElement s, string name)
    {
        if (s.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v))
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i == 1;
        return false;
    }

    /// <summary>Case-insensitive exact tag lookup on a stream.</summary>
    private static string? Tag(JsonElement s, string key)
    {
        if (s.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject())
                if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
        return null;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double? Dbl(JsonElement e, string name) => Dbl(Str(e, name));

    private static double? Dbl(string? s) =>
        s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? Trunc(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

    public static string? CleanStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        var t = Regex.Replace(stderr.Trim(), @"\s+", " ");
        return t.Length > 300 ? t[..300] : t;
    }
}
