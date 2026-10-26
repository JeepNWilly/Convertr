// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;

namespace Convertr.Core;

/// <summary>What the finished output should look like. The verifier checks the real output against this.</summary>
public sealed record ExpectedOutput
{
    /// <summary>"hevc", "h264", or the source codec when the video is copied.</summary>
    public string VideoCodec { get; init; } = "";
    public bool VideoCopied { get; init; }
    public IReadOnlyList<string> AudioCodecs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AudioLanguages { get; init; } = Array.Empty<string>();
    public int SubtitleStreams { get; init; }
    public bool Cleaned { get; init; }
    public bool ChaptersDropped { get; init; }
    public string Extension { get; init; } = "mp4";
}

/// <summary>The ffmpeg command for one job: arguments (no shell quoting needed), the output container and what to expect.</summary>
public sealed record FfmpegCommand
{
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    /// <summary>"mp4" or "mkv" (the extension of the final file).</summary>
    public string Extension { get; init; } = "mp4";
    public string Format { get; init; } = "mp4";
    public ExpectedOutput Expected { get; init; } = new();
    /// <summary>Things worth telling the user (for example "image subtitles force MKV").</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The command as one readable line (for the details dialog and the log). Arguments with spaces are quoted.</summary>
    public string ToCommandLine(string exe = "ffmpeg")
    {
        var sb = new StringBuilder(Quote(exe));
        foreach (var a in Args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    private static string Quote(string a) =>
        a.Length == 0 || a.IndexOfAny(new[] { ' ', '\t', '"', '\'', '&', '(', ')', '[', ']', ';', '%', '$', '`', '!' }) >= 0
            ? "\"" + a.Replace("\"", "\\\"") + "\""
            : a;
}

/// <summary>
/// Builds the ffmpeg command line for one plan item (DESIGN.md 3.3 to 3.6). Pure: no disk access, no process.
/// Explicit stream maps only (so attachments, cover art, data and unwanted subtitle streams are never copied), no scaling,
/// deinterlacing with bwdif only when the plan says so. Option names were checked against ffmpeg 9.0.2
/// (see docs/ENCODER_OPTIONS_VERIFIED.txt).
/// </summary>
public static class FfmpegCommandBuilder
{
    private static readonly HashSet<string> TextSubCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "ttml" };

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <param name="partPath">The temporary output (for example "Movie.mp4.convertr-part").</param>
    /// <param name="chapterMetaPath">Optional FFMETADATA file with cleaned chapter titles (becomes input 1).</param>
    public static FfmpegCommand Build(ScanEntry e, PlanItem plan, Settings s, string partPath, string? chapterMetaPath = null)
    {
        var p = e.Probe;
        var v = p.Video ?? throw new InvalidOperationException("No video stream: nothing to convert.");
        var notes = new List<string>();
        var clean = s.CleanJunkMetadata;

        // ---------- subtitles and container ----------
        var keepSubs = s.KeepEmbeddedSubsIfNoExternal && !e.HasExternalSubs && p.Subs.Count > 0;
        var ext = s.Container == OutputContainer.Mp4 ? "mp4" : "mkv";
        var subMaps = new List<int>();
        if (keepSubs)
        {
            var anyImage = p.Subs.Any(x => x.IsImage);
            if (anyImage && ext == "mp4") { ext = "mkv"; notes.Add("Image subtitles cannot go into MP4, so this file is written as MKV."); }
            for (var i = 0; i < p.Subs.Count; i++)
            {
                var sub = p.Subs[i];
                var ok = ext == "mkv" ? true : TextSubCodecs.Contains(sub.Codec);
                if (ext == "mkv" && !sub.IsImage && !TextSubCodecs.Contains(sub.Codec)) ok = false;
                if (ok) subMaps.Add(i); else notes.Add($"Subtitle track {i + 1} ({sub.Codec}) cannot be kept and is dropped.");
            }
        }
        var mp4 = ext == "mp4";

        // ---------- audio selection ----------
        var audio = p.Audio;
        var keepIdx = Enumerable.Range(0, audio.Count).ToList();
        if (s.KeepOnlyLanguages)
        {
            var langs = new HashSet<string>(s.KeepLanguages.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()));
            var filtered = keepIdx.Where(i => langs.Contains((audio[i].Language ?? "und").ToLowerInvariant())).ToList();
            if (filtered.Count > 0 && filtered.Count < keepIdx.Count) { keepIdx = filtered; notes.Add("Audio limited to: " + string.Join(", ", langs) + "."); }
        }

        var rx = MetadataAnalyzer.JunkRegex(s.JunkPatterns);
        // Intel (VAAPI / QSV) hardware encode: only for the HEVC (or opt-in AV1) re-encode; legacy H.264 targets stay on the CPU encoder there.
        var intel = s.Mode == EncoderMode.Gpu && s.GpuBackend is GpuBackend.Qsv or GpuBackend.Vaapi && plan.Action == PlanAction.ReencodeVideo
                    && (plan.Kind == ReencodeKind.Size || plan.TargetVideo == "hevc");
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y",
        };
        if (intel) args.AddRange(IntelHw.InitArgs(s));
        args.AddRange(new[] { "-i", e.FullPath });
        if (chapterMetaPath != null) { args.Add("-i"); args.Add(chapterMetaPath); }

        // ---------- maps ----------
        args.AddRange(new[] { "-map", "0:V:0" });
        var outAudio = new List<(string Codec, string Lang)>();
        var audioArgs = new List<string>();
        var audioMeta = new List<string>();
        var k = 0;
        void AddAudio(int inIdx, string spec)
        {
            args.Add("-map"); args.Add("0:a:" + I(inIdx));
            audioArgs.AddRange(spec.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var i in keepIdx)
        {
            var a = audio[i];
            var lang = string.IsNullOrWhiteSpace(a.Language) ? "und" : a.Language;
            string ak = I(k);
            if (AudioRules.IsCompatible(a))
            {
                AddAudio(i, $"-c:a:{ak} copy");
                outAudio.Add((a.Codec.ToLowerInvariant(), lang));
                if (clean)
                {
                    audioMeta.Add($"-metadata:s:a:{ak}"); audioMeta.Add("language=" + lang);
                    if (s.KeepAudioTrackTitles && !string.IsNullOrWhiteSpace(a.Title) && !rx.IsMatch(a.Title!))
                    { audioMeta.Add($"-metadata:s:a:{ak}"); audioMeta.Add("title=" + a.Title); }
                }
                k++;
            }
            else
            {
                var target = AudioRules.TargetFor(a, s.DtsOtherTarget == AudioFallback.Aac);
                string spec; string codec;
                if (target.StartsWith("AC3")) { codec = "ac3"; spec = $"-c:a:{ak} ac3 -b:a:{ak} 640k -ac:a:{ak} 6"; }
                else if (target == "AAC 384k") { codec = "aac"; spec = $"-c:a:{ak} aac -b:a:{ak} 384k" + (a.Channels >= 6 ? $" -ac:a:{ak} 6" : ""); }
                else { codec = "aac"; spec = $"-c:a:{ak} aac -b:a:{ak} 192k" + (a.Channels is 1 or 2 ? $" -ac:a:{ak} {I(a.Channels)}" : ""); }
                AddAudio(i, spec);
                outAudio.Add((codec, lang));
                if (clean) { audioMeta.Add($"-metadata:s:a:{ak}"); audioMeta.Add("language=" + lang); }
                k++;
                if (s.KeepOriginalAudioAsSecondTrack)
                {
                    var bk = I(k);
                    AddAudio(i, $"-c:a:{bk} copy -disposition:a:{bk} 0");
                    outAudio.Add((a.Codec.ToLowerInvariant(), lang));
                    if (clean) { audioMeta.Add($"-metadata:s:a:{bk}"); audioMeta.Add("language=" + lang); }
                    k++;
                }
            }
        }
        if (s.AddAacCompatTrack && keepIdx.Count > 0)
        {
            var first = audio[keepIdx[0]];
            var already = first.Codec.Equals("aac", StringComparison.OrdinalIgnoreCase) && first.Channels <= 2;
            if (!already)
            {
                var ck = I(k);
                var lang = string.IsNullOrWhiteSpace(first.Language) ? "und" : first.Language;
                AddAudio(keepIdx[0], $"-c:a:{ck} aac -b:a:{ck} 192k -ac:a:{ck} 2 -disposition:a:{ck} 0");
                outAudio.Add(("aac", lang));
                audioMeta.Add($"-metadata:s:a:{ck}"); audioMeta.Add("language=" + lang);
                audioMeta.Add($"-metadata:s:a:{ck}"); audioMeta.Add("title=AAC 2.0 (compatibility)");
                k++;
            }
        }
        foreach (var i in subMaps) { args.Add("-map"); args.Add("0:s:" + I(i)); }
        args.Add("-dn");
        if (subMaps.Count == 0) args.Add("-sn");

        // ---------- metadata and chapters ----------
        if (clean) { args.Add("-map_metadata"); args.Add("-1"); }
        else { args.Add("-map_metadata"); args.Add("0"); }
        var dropChapters = s.DropChapters;
        args.Add("-map_chapters");
        args.Add(dropChapters ? "-1" : chapterMetaPath != null ? "1" : "0");
        if (clean && !string.IsNullOrWhiteSpace(p.FormatTags.GetValueOrDefault("title")) &&
            s.KeepSensibleTitle && MetadataAnalyzer.IsSensibleTitle(p.FormatTags["title"], e.BaseName, rx))
        { args.Add("-metadata"); args.Add("title=" + p.FormatTags["title"]); }

        // ---------- video ----------
        var videoCodecOut = v.Codec.ToLowerInvariant();
        var copyVideo = plan.Action != PlanAction.ReencodeVideo;
        if (copyVideo)
        {
            args.AddRange(new[] { "-c:v", "copy" });
            if (mp4 && videoCodecOut == "hevc") args.AddRange(new[] { "-tag:v", "hvc1" });
        }
        else
        {
            var hevc = plan.Kind == ReencodeKind.Size || plan.TargetVideo == "hevc";
            videoCodecOut = hevc ? "hevc" : "h264";
            var sd = plan.Resolution is ResolutionClass.P576 or ResolutionClass.P480 or ResolutionClass.Below480;
            var gpu = s.Mode == EncoderMode.Gpu && !(s.GpuBackend is GpuBackend.Qsv or GpuBackend.Vaapi && !intel);
            if (s.Mode == EncoderMode.Gpu && !gpu) notes.Add("Intel GPU: this H.264 compatibility re-encode runs on the CPU (the Arc is used for HEVC).");
            if (intel)
            {
                var tenBitI = s.UseHevc10Bit || v.BitDepth >= 10;
                var av1 = s.GpuCodec == GpuCodec.Av1;
                var chain = new List<string>();
                if (plan.Deinterlace) chain.Add("bwdif=mode=send_frame:parity=auto:deint=all");
                chain.Add("format=" + (tenBitI ? "p010le" : "nv12"));
                chain.Add(s.GpuBackend == GpuBackend.Qsv ? "hwupload=extra_hw_frames=64" : "hwupload");
                args.AddRange(new[] { "-vf", string.Join(",", chain) });
                var q = av1 ? s.IntelAv1Cq : (s.GpuBackend == GpuBackend.Qsv ? s.IntelQsvCq : s.IntelCq) - (sd ? 1 : 0);
                args.AddRange(new[] { "-c:v", IntelHw.EncoderName(s.GpuBackend, av1) });
                args.AddRange(IntelHw.RateArgs(s, av1, q));
                if (!av1) args.AddRange(new[] { "-profile:v", tenBitI ? "main10" : "main" });
                videoCodecOut = av1 ? "av1" : "hevc";
                if (mp4 && !av1) args.AddRange(new[] { "-tag:v", "hvc1" });
            }
            else
            {
            if (plan.Deinterlace) args.AddRange(new[] { "-vf", "bwdif=mode=send_frame:parity=auto:deint=all" });
            if (hevc)
            {
                var tenBit = s.UseHevc10Bit || v.BitDepth >= 10;
                var pix = tenBit ? (gpu ? "p010le" : "yuv420p10le") : "yuv420p";
                if (gpu)
                {
                    var cq = s.GpuCq - (sd ? 1 : 0);
                    args.AddRange(new[] { "-c:v", "hevc_nvenc", "-preset", "p7", "-tune", "hq", "-rc", "vbr", "-cq", I(cq), "-b:v", "0",
                        "-spatial-aq", "1", "-rc-lookahead", "32", "-profile:v", tenBit ? "main10" : "main", "-pix_fmt", pix });
                }
                else
                {
                    var crf = s.CpuCrf - (sd ? 1 : 0);
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", s.X265Preset, "-crf", I(crf), "-pix_fmt", pix, "-x265-params", s.X265Threads > 0 ? "aq-mode=3:pools=" + I(s.X265Threads) : "aq-mode=3" });
                }
                if (mp4) args.AddRange(new[] { "-tag:v", "hvc1" });
            }
            else
            {
                var hd = plan.Resolution is ResolutionClass.P720 or ResolutionClass.P1080 or ResolutionClass.K4;
                var crf = s.LegacyCrf - (hd ? 1 : 0);
                if (gpu)
                    args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p7", "-tune", "hq", "-rc", "vbr", "-cq", I(crf + 1), "-b:v", "0",
                        "-spatial-aq", "1", "-rc-lookahead", "32", "-profile:v", "high", "-pix_fmt", "yuv420p" });
                else
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "slow", "-crf", I(crf), "-profile:v", "high", "-level", "4.1", "-pix_fmt", "yuv420p" });
            }
            }
        }

        // ---------- audio / subtitle codec args, metadata, muxer ----------
        args.AddRange(audioArgs);
        if (subMaps.Count > 0)
        {
            if (mp4) args.AddRange(new[] { "-c:s", "mov_text" }); else args.AddRange(new[] { "-c:s", "copy" });
            if (clean)
                for (var j = 0; j < subMaps.Count; j++)
                {
                    var lang = string.IsNullOrWhiteSpace(p.Subs[subMaps[j]].Language) ? "und" : p.Subs[subMaps[j]].Language;
                    args.Add($"-metadata:s:s:{I(j)}"); args.Add("language=" + lang);
                }
        }
        if (clean)
        {
            args.AddRange(audioMeta);
            args.AddRange(new[] { "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact" });
        }
        if (mp4) args.AddRange(new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-max_muxing_queue_size", "9999", "-f", mp4 ? "mp4" : "matroska", partPath });

        return new FfmpegCommand
        {
            Args = args,
            Extension = ext,
            Format = mp4 ? "mp4" : "matroska",
            Notes = notes,
            Expected = new ExpectedOutput
            {
                VideoCodec = videoCodecOut,
                VideoCopied = copyVideo,
                AudioCodecs = outAudio.Select(a => a.Codec).ToList(),
                AudioLanguages = outAudio.Select(a => a.Lang).ToList(),
                SubtitleStreams = subMaps.Count,
                Cleaned = clean,
                ChaptersDropped = dropChapters,
                Extension = ext,
            },
        };
    }
}

/// <summary>Intel (VAAPI / QSV) pieces of the ffmpeg command line. Option sets verified on the Arc Pro B60 by IntelProbe / tools/intel-bench.sh.</summary>
public static class IntelHw
{
    public const string DefaultDevice = "/dev/dri/renderD128";

    public static string Device(Settings s) => !string.IsNullOrWhiteSpace(s.GpuDevice) ? s.GpuDevice : FirstRenderNode() ?? DefaultDevice;

    public static string? FirstRenderNode()
    {
        try
        {
            if (!Directory.Exists("/dev/dri")) return null;
            return Directory.GetFiles("/dev/dri", "renderD*").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        }
        catch { return null; }
    }

    public static IEnumerable<string> InitArgs(Settings s)
    {
        var dev = Device(s);
        if (s.GpuBackend == GpuBackend.Qsv)
            return new[] { "-init_hw_device", "vaapi=va:" + dev, "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs" };
        return new[] { "-init_hw_device", "vaapi=va:" + dev, "-filter_hw_device", "va" };
    }

    public static string EncoderName(GpuBackend b, bool av1) => (av1 ? "av1_" : "hevc_") + (b == GpuBackend.Qsv ? "qsv" : "vaapi");

    /// <summary>Default rate-control options per backend/codec ({Q} = quality). Candidates are tried in this order by the encoder test.</summary>
    public static IReadOnlyList<string> Candidates(GpuBackend b, bool av1) => (b, av1) switch
    {
        (GpuBackend.Vaapi, false) => new[] { "-rc_mode ICQ -global_quality {Q}", "-rc_mode ICQ -global_quality {Q} -low_power 1", "-rc_mode CQP -qp {Q}", "-rc_mode CQP -qp {Q} -low_power 1" },
        (GpuBackend.Qsv, false) => new[] { "-preset veryslow -global_quality {Q} -look_ahead 1 -look_ahead_depth 40", "-preset veryslow -global_quality {Q}", "-global_quality {Q}" },
        (GpuBackend.Qsv, true) => new[] { "-preset veryslow -global_quality {Q}", "-global_quality {Q}" },
        _ => new[] { "-rc_mode CQP -global_quality {Q}", "-rc_mode CQP -global_quality {Q} -low_power 1", "-rc_mode CQP -qp {Q}" },
    };

    public static IEnumerable<string> RateArgs(Settings s, bool av1, int quality)
    {
        var tpl = !string.IsNullOrWhiteSpace(s.GpuEncoderArgs) ? s.GpuEncoderArgs : Candidates(s.GpuBackend, av1)[0];
        return tpl.Replace("{Q}", quality.ToString(CultureInfo.InvariantCulture)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
