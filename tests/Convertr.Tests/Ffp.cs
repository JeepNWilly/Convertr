// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;
using Convertr.Core;

namespace Convertr.Tests;

/// <summary>Builds mocked ffprobe JSON (same shape as real ffprobe output) and ScanEntry objects from it.</summary>
public static class Ffp
{
    private static string I(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Tags(IDictionary<string, string>? t)
    {
        if (t == null || t.Count == 0) return "";
        return ",\"tags\":{" + string.Join(",", t.Select(kv => $"\"{Esc(kv.Key)}\":\"{Esc(kv.Value)}\"")) + "}";
    }

    public static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static string Video(string codec, int w, int h, long? bitRate = null, string pix = "yuv420p", string? bps = null,
        string fieldOrder = "progressive", string? transfer = null, bool dovi = false, string rate = "24000/1001",
        string? bitsRaw = null, string? title = null, string? profile = "High")
    {
        var sb = new StringBuilder("{\"index\":0,\"codec_name\":\"" + codec + "\",\"codec_type\":\"video\"");
        if (profile != null) sb.Append($",\"profile\":\"{profile}\"");
        sb.Append($",\"width\":{w},\"height\":{h},\"pix_fmt\":\"{pix}\",\"field_order\":\"{fieldOrder}\"");
        if (bitsRaw != null) sb.Append($",\"bits_per_raw_sample\":\"{bitsRaw}\"");
        if (transfer != null) sb.Append($",\"color_transfer\":\"{transfer}\"");
        sb.Append($",\"r_frame_rate\":\"{rate}\",\"avg_frame_rate\":\"{rate}\"");
        if (bitRate != null) sb.Append($",\"bit_rate\":\"{bitRate}\"");
        if (dovi) sb.Append(",\"side_data_list\":[{\"side_data_type\":\"DOVI configuration record\"}]");
        sb.Append(",\"disposition\":{\"default\":1,\"attached_pic\":0}");
        var tags = new Dictionary<string, string>();
        if (bps != null) tags["BPS"] = bps;
        if (title != null) tags["title"] = title;
        sb.Append(Tags(tags)).Append('}');
        return sb.ToString();
    }

    public static string CoverArt() =>
        "{\"index\":9,\"codec_name\":\"mjpeg\",\"codec_type\":\"video\",\"width\":600,\"height\":900,\"disposition\":{\"attached_pic\":1}}";

    public static string Audio(string codec, int channels, string lang = "eng", long? bitRate = null, string? title = null,
        string? profile = null, bool isDefault = false, string? bps = null)
    {
        var sb = new StringBuilder("{\"index\":1,\"codec_name\":\"" + codec + "\",\"codec_type\":\"audio\"");
        if (profile != null) sb.Append($",\"profile\":\"{profile}\"");
        sb.Append($",\"channels\":{channels}");
        if (bitRate != null) sb.Append($",\"bit_rate\":\"{bitRate}\"");
        sb.Append($",\"disposition\":{{\"default\":{(isDefault ? 1 : 0)}}}");
        var tags = new Dictionary<string, string> { ["language"] = lang };
        if (title != null) tags["title"] = title;
        if (bps != null) tags["BPS"] = bps;
        sb.Append(Tags(tags)).Append('}');
        return sb.ToString();
    }

    public static string Sub(string codec, string lang = "eng") =>
        "{\"index\":2,\"codec_name\":\"" + codec + "\",\"codec_type\":\"subtitle\",\"disposition\":{\"forced\":0},\"tags\":{\"language\":\"" + lang + "\"}}";

    public static string Attachment(string name) =>
        "{\"index\":5,\"codec_name\":\"ttf\",\"codec_type\":\"attachment\",\"tags\":{\"filename\":\"" + name + "\",\"mimetype\":\"font/ttf\"}}";

    public static string Data() => "{\"index\":6,\"codec_name\":\"bin_data\",\"codec_type\":\"data\"}";

    public static string StatsAudio(string codec, int ch) =>
        "{\"index\":1,\"codec_name\":\"" + codec + "\",\"codec_type\":\"audio\",\"channels\":" + ch +
        ",\"tags\":{\"language\":\"eng\",\"BPS\":\"384000\",\"_STATISTICS_WRITING_APP\":\"mkvmerge v70\",\"NUMBER_OF_FRAMES\":\"100\"}}";

    public static string Json(string formatName, double? durationSec, long? bitRate, IEnumerable<string> streams,
        IDictionary<string, string>? formatTags = null, IEnumerable<string>? chapterTitles = null)
    {
        var sb = new StringBuilder("{\"streams\":[" + string.Join(",", streams) + "],\"format\":{\"filename\":\"x\",\"nb_streams\":1,\"format_name\":\"" + formatName + "\"");
        if (durationSec != null) sb.Append($",\"duration\":\"{I(durationSec.Value)}\"");
        if (bitRate != null) sb.Append($",\"bit_rate\":\"{bitRate}\"");
        sb.Append(Tags(formatTags)).Append("}");
        sb.Append(",\"chapters\":[");
        if (chapterTitles != null)
            sb.Append(string.Join(",", chapterTitles.Select((t, i) =>
                "{\"id\":" + i + ",\"start\":0,\"end\":1,\"tags\":" + (t == null ? "{}" : "{\"title\":\"" + Esc(t) + "\"}") + "}")));
        sb.Append("]}");
        return sb.ToString();
    }

    public const string Mkv = "matroska,webm";
    public const string Mp4 = "mov,mp4,m4a,3gp,3g2,mj2";

    public static ScanEntry Entry(string fileName, string json, long sizeBytes = 1_000_000_000, string? stderr = null,
        bool externalSubs = false, string library = "Movies")
    {
        return new ScanEntry
        {
            Library = library,
            RelativePath = fileName,
            FullPath = "D:\\" + fileName,
            SizeBytes = sizeBytes,
            HasExternalSubs = externalSubs,
            Probe = ProbeParser.Parse(json, stderr),
        };
    }

    /// <summary>Common case: one video, one audio.</summary>
    public static ScanEntry Simple(string fileName, string format, string codec, int w, int h, long? videoBps, string audioCodec = "ac3",
        int audioCh = 6, long sizeBytes = 1_000_000_000, double duration = 6000, string fieldOrder = "progressive", string? bps = null,
        IEnumerable<string>? extra = null, string pix = "yuv420p", IDictionary<string, string>? formatTags = null,
        IEnumerable<string>? chapters = null, bool externalSubs = false, string? stderr = null)
    {
        var streams = new List<string> { Video(codec, w, h, videoBps, pix: pix, bps: bps, fieldOrder: fieldOrder) };
        streams.Add(Audio(audioCodec, audioCh, bitRate: 384000));
        if (extra != null) streams.AddRange(extra);
        long? overall = videoBps != null ? videoBps + 384000 : null;
        return Entry(fileName, Json(format, duration, overall, streams, formatTags, chapters), sizeBytes, stderr, externalSubs);
    }
}
