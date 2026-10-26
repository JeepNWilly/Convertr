// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text.Json.Serialization;

namespace Convertr.Core;

/// <summary>What the planner decided to do with a file (decision table, DESIGN.md 3.2).</summary>
public enum PlanAction
{
    Skip,
    Remux,
    ReencodeVideo,
    FixAudioOnly,
    Error,
    Review,
}

/// <summary>Why a file is re-encoded: compatibility (legacy codec) or size (high-bitrate H.264).</summary>
public enum ReencodeKind
{
    None,
    Compat,
    Size,
}

/// <summary>Colour theme: follow Windows (default), or force Light or Dark.</summary>
public enum ThemeMode { System, Light, Dark }

public static class ThemeResolver
{
    /// <summary>Dark when forced, or when "System" is chosen and Windows apps are in dark mode.</summary>
    public static bool IsDark(ThemeMode mode, bool systemPrefersDark) => mode == ThemeMode.Dark || (mode == ThemeMode.System && systemPrefersDark);
}

public enum EncoderMode { Gpu, Cpu }

/// <summary>Which hardware encoder API the GPU mode uses. Nvenc is today's behaviour; Qsv and Vaapi are added for Intel on Linux (Convertr_svc).</summary>
public enum GpuBackend { Nvenc, Auto, Qsv, Vaapi }

/// <summary>Video codec of the GPU mode. Hevc is today's behaviour; Av1 is opt-in (many TVs cannot play it).</summary>
public enum GpuCodec { Hevc, Av1 }

/// <summary>How a result copied back from the scratch disk is checked.</summary>
public enum CopyBackVerify { SizeAndProbe, Checksum }

public enum OutputContainer { Mp4, Mkv }

public enum OriginalsHandling { MoveToHolding, RenameInPlace, DeleteAfterVerify }

public enum AudioFallback { Ac3, Aac }

public enum ResolutionClass { Unknown, Below480, P480, P576, P720, P1080, K4 }

public static class ResolutionClassifier
{
    /// <summary>Same buckets as the library scan (DESIGN.md section 2): 4K, 1080p, 720p, 576p, 480p, Below 480p.</summary>
    public static ResolutionClass Classify(int width, int height)
    {
        if (width <= 0 && height <= 0) return ResolutionClass.Unknown;
        if (width >= 3500 || height >= 2000) return ResolutionClass.K4;
        if (width >= 1800 || height >= 1000) return ResolutionClass.P1080;
        if (width >= 1200 || height >= 700) return ResolutionClass.P720;
        if (height >= 560) return ResolutionClass.P576;
        if (height >= 400 || width >= 600) return ResolutionClass.P480;
        return ResolutionClass.Below480;
    }

    public static string Label(ResolutionClass r) => r switch
    {
        ResolutionClass.K4 => "4K",
        ResolutionClass.P1080 => "1080p",
        ResolutionClass.P720 => "720p",
        ResolutionClass.P576 => "576p",
        ResolutionClass.P480 => "480p",
        ResolutionClass.Below480 => "Below 480p",
        _ => "?",
    };
}

public sealed class VideoInfo
{
    public string Codec { get; set; } = "";
    public string? Profile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? PixFmt { get; set; }
    public int BitDepth { get; set; }
    public double? Fps { get; set; }
    public string? FieldOrder { get; set; }
    public string? ColorTransfer { get; set; }
    /// <summary>HDR10, HLG, DolbyVision (joined with +), or empty.</summary>
    public string Hdr { get; set; } = "";
    public long? Kbps { get; set; }
    /// <summary>stream, tag or estimated (or empty when unknown).</summary>
    public string KbpsSource { get; set; } = "";
    public string? Title { get; set; }
    public string? HandlerName { get; set; }

    [JsonIgnore]
    public ResolutionClass Resolution => ResolutionClassifier.Classify(Width, Height);

    [JsonIgnore]
    public bool Interlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";
}

public sealed class AudioInfo
{
    public string Codec { get; set; } = "";
    public string? Profile { get; set; }
    public int Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public string Language { get; set; } = "und";
    public string? Title { get; set; }
    public string? HandlerName { get; set; }
    public bool IsDefault { get; set; }
    public long? Kbps { get; set; }

    public string Describe()
    {
        var ch = Channels switch { 1 => "mono", 2 => "2.0", 6 => "5.1", 8 => "7.1", 0 => "?ch", _ => Channels + "ch" };
        return Codec + " " + ch;
    }
}

public sealed class SubInfo
{
    public string Codec { get; set; } = "";
    public string Language { get; set; } = "und";
    public string? Title { get; set; }
    public string? HandlerName { get; set; }
    public bool IsImage { get; set; }
    public bool Forced { get; set; }
}

/// <summary>Everything the planner needs from one ffprobe run. This is what the probe cache stores.</summary>
public sealed class ProbeInfo
{
    /// <summary>Non-null when the probe failed (rule 1).</summary>
    public string? ProbeError { get; set; }
    /// <summary>ffprobe stderr (warnings), collapsed and truncated.</summary>
    public string? Warnings { get; set; }
    public string? FormatName { get; set; }
    public double? DurationSec { get; set; }
    public long? OverallKbps { get; set; }
    public VideoInfo? Video { get; set; }
    public List<AudioInfo> Audio { get; set; } = new();
    public List<SubInfo> Subs { get; set; } = new();
    public int ExtraVideoStreams { get; set; }
    public int AttachmentCount { get; set; }
    public int AttachedPicCount { get; set; }
    public int DataStreamCount { get; set; }
    /// <summary>Number of streams carrying mkvmerge _STATISTICS_* tags.</summary>
    public int StatsTagStreams { get; set; }
    public Dictionary<string, string> FormatTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int ChapterCount { get; set; }
    public List<string> ChapterTitles { get; set; } = new();
}

/// <summary>One video file in the library: where it is, how big, whether it has external subs, and its probe.</summary>
public sealed class ScanEntry
{
    public string Library { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public long MtimeUtcTicks { get; set; }
    public bool HasExternalSubs { get; set; }
    public ProbeInfo Probe { get; set; } = new();

    [JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(RelativePath.Replace('\\', '/'));

    [JsonIgnore]
    public string BaseName => System.IO.Path.GetFileNameWithoutExtension(FileName);

    [JsonIgnore]
    public string Extension => System.IO.Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();
}
