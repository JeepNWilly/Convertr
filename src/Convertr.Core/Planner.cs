// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;

namespace Convertr.Core;

/// <summary>The planner's decision for one file.</summary>
public sealed record PlanItem
{
    public PlanAction Action { get; init; }
    public ReencodeKind Kind { get; init; }
    /// <summary>Decision-table rule number (1 to 8) that matched.</summary>
    public int Rule { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>Interlaced source that will be deinterlaced (bwdif) when converted (re-encode actions only).</summary>
    public bool Deinterlace { get; init; }
    public long EstSaveLowBytes { get; init; }
    public long EstSaveHighBytes { get; init; }
    /// <summary>Expected saving (between low and high). 0 when the model predicts the result would be refused by the keep-if-smaller rule.</summary>
    public long EstSaveMidBytes { get; init; }
    /// <summary>The expected result fails the keep-if-smaller rule: the encode would probably be thrown away.</summary>
    public bool LikelyRefused { get; init; }
    /// <summary>True when the rewrite also converts at least one audio track.</summary>
    public bool AudioConversion { get; init; }
    /// <summary>True when embedded subtitles would be removed by the rewrite.</summary>
    public bool StripsSubs { get; init; }
    public MetadataFindings Metadata { get; init; } = new();
    public ResolutionClass Resolution { get; init; }
    public bool HasWarnings { get; init; }
    /// <summary>"h264" or "hevc" for re-encodes, otherwise empty.</summary>
    public string TargetVideo { get; init; } = "";

    /// <summary>True when the action would write the file in M2 (the planner never writes anything).</summary>
    public bool WritesFile => Action is PlanAction.ReencodeVideo or PlanAction.FixAudioOnly or PlanAction.Remux;

    public string ActionLabel => Action switch
    {
        PlanAction.Skip => "Skip",
        PlanAction.Remux => "Remux",
        PlanAction.ReencodeVideo => "Re-encode video",
        PlanAction.FixAudioOnly => "Fix audio only",
        PlanAction.Error => "Error",
        PlanAction.Review => "Review",
        _ => Action.ToString(),
    };
}

/// <summary>
/// The decision table (DESIGN.md 3.2) as pure functions: probe result + options in, <see cref="PlanItem"/> out.
/// First matching rule wins. No disk access.
/// </summary>
public static class Planner
{
    private static readonly HashSet<string> LegacyCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mpeg4", "msmpeg4v1", "msmpeg4v2", "msmpeg4v3", "msmpeg4", "vc1", "wmv1", "wmv2", "wmv3",
        "mpeg1video", "mpeg2video", "h263", "h263p", "flv1", "vp6", "vp6f", "vp6a",
        "rv10", "rv20", "rv30", "rv40", "svq1", "svq3", "cinepak", "indeo2", "indeo3", "indeo4", "indeo5",
    };

    public static bool IsLegacyCodec(string codec) => LegacyCodecs.Contains(codec);

    public enum ContainerFamily { Mkv, Mp4, Avi, Asf, Mpeg, Other }

    public static ContainerFamily FamilyOf(ProbeInfo p, string extension)
    {
        var f = (p.FormatName ?? "").ToLowerInvariant();
        if (f.Contains("matroska")) return ContainerFamily.Mkv;
        if (f.Contains("mp4") || f.Contains("mov")) return ContainerFamily.Mp4;
        if (f.Split(',').Contains("avi")) return ContainerFamily.Avi;
        if (f.Split(',').Contains("asf")) return ContainerFamily.Asf;
        if (f is "mpeg" or "mpegvideo") return ContainerFamily.Mpeg;
        return extension switch
        {
            "mkv" => ContainerFamily.Mkv,
            "mp4" or "m4v" or "mov" => ContainerFamily.Mp4,
            "avi" => ContainerFamily.Avi,
            "wmv" or "asf" => ContainerFamily.Asf,
            "mpg" or "mpeg" => ContainerFamily.Mpeg,
            _ => ContainerFamily.Other,
        };
    }

    private static bool IsLegacyContainer(ContainerFamily c) => c is ContainerFamily.Avi or ContainerFamily.Asf or ContainerFamily.Mpeg;

    /// <summary>Would embedded subtitles be removed if this file were rewritten?</summary>
    public static bool StripsSubtitles(ScanEntry e, PlannerOptions o)
    {
        if (e.Probe.Subs.Count == 0) return false;
        if (o.KeepEmbeddedSubsIfNoExternal && !e.HasExternalSubs) return false;
        return true;
    }

    public static PlanItem Plan(ScanEntry e, PlannerOptions o)
    {
        var p = e.Probe;
        var warn = !string.IsNullOrEmpty(p.Warnings);

        // Rule 1: probe failed, no video stream, duration missing.
        if (p.ProbeError != null) return Err(p.ProbeError, warn);
        if (p.Video == null) return Err("No video stream found", warn);
        if (p.DurationSec is null or <= 0) return Err("Duration missing (file looks incomplete)", warn);

        var v = p.Video;
        var res = v.Resolution;
        var meta = MetadataAnalyzer.Analyze(e, o);
        var strips = StripsSubtitles(e, o);
        var container = FamilyOf(p, e.Extension);
        var converts = AudioRules.ToConvert(p).ToList();

        var interlaced = v.Interlaced;
        const string DeintNote = "; interlaced: deinterlace (bwdif) will be applied in conversion";

        PlanItem Make(PlanAction action, ReencodeKind kind, int rule, string reason, string target = "", SavingsEstimate est = default, bool audio = false) => new()
        {
            Action = action,
            Kind = kind,
            Rule = rule,
            Reason = reason + (interlaced && action == PlanAction.ReencodeVideo ? DeintNote : ""),
            Deinterlace = interlaced && action == PlanAction.ReencodeVideo,
            TargetVideo = target,
            EstSaveLowBytes = est.LowBytes,
            EstSaveHighBytes = est.HighBytes,
            EstSaveMidBytes = est.MidBytes,
            LikelyRefused = est.LikelyRefused,
            AudioConversion = audio,
            StripsSubs = strips,
            Metadata = meta,
            Resolution = res,
            HasWarnings = warn,
        };

        // Rule 2: HDR / Dolby Vision / 4K unless the user opted in; interlaced only when "process interlaced" is switched off.
        {
            var why = new List<string>();
            if (!o.AllowHdr4k)
            {
                if (!string.IsNullOrEmpty(v.Hdr)) why.Add(v.Hdr);
                if (res == ResolutionClass.K4) why.Add("4K");
            }
            if (interlaced && !o.ProcessInterlaced) why.Add("interlaced");
            if (why.Count > 0)
                return Make(PlanAction.Review, ReencodeKind.None, 2, string.Join(" + ", why) + ": needs your decision (not handled automatically)");
        }

        var codec = v.Codec.ToLowerInvariant();
        var legacyContainer = IsLegacyContainer(container);

        // Rule 3: legacy video codec (or legacy container holding something other than H.264/HEVC) -> H.264 for compatibility.
        if (IsLegacyCodec(codec) || (legacyContainer && codec is not ("h264" or "hevc")))
        {
            var guard = AmbiguousAudio(converts);
            if (guard != null) return Make(PlanAction.Review, ReencodeKind.None, 3, guard);
            var reason = $"Legacy {codec} in {ContainerLabel(container)}: re-encode to H.264 for compatibility" + AudioNote(converts, o);
            return Make(PlanAction.ReencodeVideo, ReencodeKind.Compat, 3, reason, "h264", audio: converts.Count > 0);
        }

        if (codec is not ("h264" or "hevc"))
            return Make(PlanAction.Review, ReencodeKind.None, 8, $"Video codec {codec} is not covered by the rules");

        string skipReason;
        var skipRule = 8;
        if (codec == "hevc")
        {
            // Rule 4: HEVC is already efficient.
            skipReason = $"HEVC {(v.BitDepth > 0 ? v.BitDepth + "-bit " : "")}already efficient";
            skipRule = 4;
        }
        else
        {
            // Rule 5: H.264 at or above the bitrate threshold for its resolution -> HEVC.
            if (v.Kbps is null)
                return Make(PlanAction.Review, ReencodeKind.None, 5, "Video bitrate unknown: cannot judge whether a re-encode is worthwhile");
            var thr = o.ThresholdFor(res);
            if (v.Kbps >= thr)
            {
                var guard = AmbiguousAudio(converts);
                if (guard != null) return Make(PlanAction.Review, ReencodeKind.None, 5, guard);
                var estimate = SavingsModel.Estimate(v.Kbps.Value, v.Width, v.Height, p.DurationSec ?? 0, e.SizeBytes, o);
                var est = v.KbpsSource == "estimated" ? "~" : "";
                var reason = $"H.264 {est}{N(v.Kbps.Value)} kbps is at or above {N(thr)} kbps ({ResolutionClassifier.Label(res)}): re-encode to HEVC" + AudioNote(converts, o);
                return Make(PlanAction.ReencodeVideo, ReencodeKind.Size, 5, reason, "hevc",
                    estimate, converts.Count > 0);
            }
            skipReason = $"H.264 {(v.KbpsSource == "estimated" ? "~" : "")}{N(v.Kbps.Value)} kbps is below {N(thr)} kbps ({ResolutionClassifier.Label(res)}): efficient enough";
        }

        // Rule 6: the video stays as it is, but no audio track is AAC/AC3/EAC3/MP3.
        if (AudioRules.NeedsFix(p))
        {
            var guard = AmbiguousAudio(converts);
            if (guard != null) return Make(PlanAction.Review, ReencodeKind.None, 6, guard);
            return Make(PlanAction.FixAudioOnly, ReencodeKind.None, 6,
                "Video can stay as it is; " + AudioNote(converts, o).TrimStart(';', ' '), audio: true);
        }

        // Rule 7: remux only (container not MP4/MKV, or optional cleanup of skipped files).
        var remux = new List<string>();
        if (container is not (ContainerFamily.Mkv or ContainerFamily.Mp4)) remux.Add($"container {ContainerLabel(container)} is not MP4/MKV");
        if (o.CleanMetadataOnSkipped && o.CleanJunkMetadata && !meta.IsClean) remux.Add("clean junk metadata");
        if (o.StripSubsOnSkipped && strips) remux.Add("strip embedded subtitles");
        if (remux.Count > 0)
            return Make(PlanAction.Remux, ReencodeKind.None, 7, "Remux without re-encoding: " + string.Join(", ", remux));

        // Rule 8: leave it alone.
        return Make(PlanAction.Skip, ReencodeKind.None, skipRule, skipReason);
    }

    public static IReadOnlyList<PlanItem> PlanAll(IEnumerable<ScanEntry> entries, PlannerOptions o) =>
        entries.Select(e => Plan(e, o)).ToList();

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static PlanItem Err(string message, bool warn) => new()
    {
        Action = PlanAction.Error,
        Rule = 1,
        Reason = message,
        HasWarnings = warn,
    };

    private static string? AmbiguousAudio(List<AudioInfo> converts)
    {
        var a = converts.FirstOrDefault(AudioRules.AmbiguousLayout);
        return a == null ? null : $"Audio {a.Codec} has {(a.Channels == 0 ? "an unknown number of" : a.Channels.ToString(CultureInfo.InvariantCulture))} channels: pick a layout before converting";
    }

    private static string AudioNote(List<AudioInfo> converts, PlannerOptions o)
    {
        if (converts.Count == 0) return "";
        var a = converts[0];
        var more = converts.Count > 1 ? $" (+{converts.Count - 1} more)" : "";
        return $"; audio {a.Describe()} to {AudioRules.TargetFor(a, o.PreferAacForSurround)}{more}";
    }

    private static string ContainerLabel(ContainerFamily c) => c switch
    {
        ContainerFamily.Mkv => "MKV",
        ContainerFamily.Mp4 => "MP4",
        ContainerFamily.Avi => "AVI",
        ContainerFamily.Asf => "WMV/ASF",
        ContainerFamily.Mpeg => "MPG",
        _ => "unknown container",
    };
}
