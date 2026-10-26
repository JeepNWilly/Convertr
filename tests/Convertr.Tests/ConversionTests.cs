// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

/// <summary>Helpers: real temp files for the swap/undo/engine tests and a fake ffmpeg.</summary>
internal static class Fx
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "convertr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes a file of the given size and returns a ScanEntry whose probe is a typical 1080p H.264 + AC3 file.</summary>
    public static ScanEntry RealEntry(string mediaDir, string rel, long size, DateTime? mtime = null, string codec = "h264", long videoBps = 9_000_000,
        string audio = "ac3", string fieldOrder = "progressive", int w = 1920, int h = 1080, double duration = 600, string format = Ffp.Mkv)
    {
        var full = Path.Combine(mediaDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var fs = File.Create(full)) { fs.SetLength(size); }
        var t = mtime ?? new DateTime(2019, 5, 17, 20, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(full, t);
        File.SetCreationTimeUtc(full, t.AddDays(-3));
        var e = Ffp.Simple(rel, format, codec, w, h, videoBps, audio, 6, size, duration, fieldOrder);
        e.FullPath = full;
        e.RelativePath = rel;
        e.MtimeUtcTicks = File.GetLastWriteTimeUtc(full).Ticks;
        return e;
    }

    public static string OutputJson(string codec, string audio, double duration, int w = 1920, int h = 1080) =>
        Ffp.Json(Ffp.Mp4, duration, null, new[] { Ffp.Video(codec, w, h, 5_000_000), Ffp.Audio(audio, 6) });
}

internal sealed class FakeFfmpeg : IFfmpegRunner
{
    public Func<IReadOnlyList<string>, long> OutputSize { get; set; } = _ => 400_000;
    public int ExitCode { get; set; }
    public string Stderr { get; set; } = "";
    public bool Hang { get; set; }
    public bool DecodeFails { get; set; }
    public List<IReadOnlyList<string>> Calls { get; } = new();
    public Action<IReadOnlyList<string>>? OnEncode { get; set; }

    public async Task<FfmpegRunResult> RunAsync(IReadOnlyList<string> args, IProgress<ProgressSnapshot>? progress, ProcessControl? control, CancellationToken ct)
    {
        Calls.Add(args);
        if (args.Contains("null") && args.Contains("-nostdin"))
            return DecodeFails ? new FfmpegRunResult(1, "Invalid data found when processing input", false) : new FfmpegRunResult(0, "", false);
        var part = args[^1];
        OnEncode?.Invoke(args);
        using (var fs = File.Create(part)) fs.SetLength(ExitCode == 0 || Hang ? OutputSize(args) : 1000);
        progress?.Report(new ProgressSnapshot(100, 2.0, 50, 1000, false));
        if (Hang) { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { return new FfmpegRunResult(-1, "", true); } }
        progress?.Report(new ProgressSnapshot(600, 2.0, 50, 1000, true));
        return new FfmpegRunResult(ExitCode, Stderr, false);
    }
}

internal sealed class FakeProbe : IProbeRunner
{
    public Func<string, string> JsonFor { get; set; } = _ => "{}";
    public Task<ProbeRunResult> RunAsync(string path, CancellationToken ct) => Task.FromResult(new ProbeRunResult(JsonFor(path), "", 0, false));
}

public class CommandBuilderTests
{
    private static Settings Cfg(Action<Settings>? f = null)
    {
        var s = new Settings { Mode = EncoderMode.Cpu };
        f?.Invoke(s);
        return s;
    }

    private static (FfmpegCommand Cmd, PlanItem Plan) B(ScanEntry e, Settings s)
    {
        var plan = Planner.Plan(e, s.ToPlannerOptions());
        return (FfmpegCommandBuilder.Build(e, plan, s, "D:\\M\\Movie.mp4.convertr-part"), plan);
    }

    private static ScanEntry Hd264(string name = "Movie (2019).mkv", string audio = "ac3", string fo = "progressive") =>
        Ffp.Simple(name, Ffp.Mkv, "h264", 1920, 1080, 9_000_000, audio, 6, 5_000_000_000, 6000, fo);

    [Fact]
    public void Golden_Hd264_Cpu_HevcCrf21_Mp4()
    {
        var (c, p) = B(Hd264(), Cfg());
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Equal(
            "ffmpeg -hide_banner -nostdin -nostats -loglevel error -progress pipe:1 -y -i \"D:\\Movie (2019).mkv\" -map 0:V:0 -map 0:a:0 -dn -sn -map_metadata -1 -map_chapters 0 " +
            "-c:v libx265 -preset slow -crf 21 -pix_fmt yuv420p -x265-params aq-mode=3 -tag:v hvc1 -c:a:0 copy -metadata:s:a:0 language=eng " +
            "-fflags +bitexact -flags:v +bitexact -flags:a +bitexact -movflags +faststart -max_muxing_queue_size 9999 -f mp4 D:\\M\\Movie.mp4.convertr-part",
            c.ToCommandLine());
    }

    [Fact]
    public void Golden_Hd264_Gpu_Nvenc()
    {
        var (c, _) = B(Hd264(), Cfg(s => s.Mode = EncoderMode.Gpu));
        Assert.Contains("-c:v hevc_nvenc -preset p7 -tune hq -rc vbr -cq 27 -b:v 0 -spatial-aq 1 -rc-lookahead 32 -profile:v main -pix_fmt yuv420p -tag:v hvc1", c.ToCommandLine());
    }

    [Fact]
    public void LegacySd_IsH264_Crf20_High41()
    {
        var e = Ffp.Simple("Old.avi", "avi", "mpeg4", 720, 576, 2_000_000, "mp3", 2, 700_000_000, 5000);
        var (c, p) = B(e, Cfg());
        Assert.Equal(ReencodeKind.Compat, p.Kind);
        Assert.Contains("-c:v libx264 -preset slow -crf 20 -profile:v high -level 4.1 -pix_fmt yuv420p", c.ToCommandLine());
        Assert.DoesNotContain("hvc1", c.ToCommandLine());
        Assert.Equal("h264", c.Expected.VideoCodec);
    }

    [Fact]
    public void LegacySd_Gpu_UsesH264Nvenc()
    {
        var e = Ffp.Simple("Old.avi", "avi", "mpeg4", 720, 576, 2_000_000, "mp3", 2, 700_000_000, 5000);
        var (c, _) = B(e, Cfg(s => s.Mode = EncoderMode.Gpu));
        Assert.Contains("-c:v h264_nvenc -preset p7 -tune hq -rc vbr -cq 21 -b:v 0", c.ToCommandLine());
    }

    [Fact]
    public void SdHevcSource_GetsOneStepLowerQuantizer()
    {
        var e = Ffp.Simple("Sd.mkv", Ffp.Mkv, "h264", 720, 576, 4_000_000, "ac3", 6, 3_000_000_000, 6000);
        var (c, p) = B(e, Cfg());
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Contains("-c:v libx265 -preset slow -crf 20 ", c.ToCommandLine());
    }

    [Fact]
    public void Interlaced_AddsBwdifOnly_WhenFlagged()
    {
        var (c, p) = B(Hd264(fo: "tt"), Cfg());
        Assert.True(p.Deinterlace);
        Assert.Contains("-vf bwdif=mode=send_frame:parity=auto:deint=all", c.ToCommandLine());
        var (c2, p2) = B(Hd264(), Cfg());
        Assert.False(p2.Deinterlace);
        Assert.DoesNotContain("bwdif", c2.ToCommandLine());
    }

    [Fact]
    public void NeverScales()
    {
        var (c, _) = B(Hd264(), Cfg());
        Assert.DoesNotContain("scale", c.ToCommandLine());
    }

    [Fact]
    public void DtsAudio_BecomesAc3_640k_FiveOne()
    {
        var (c, _) = B(Hd264(audio: "dts"), Cfg());
        Assert.Contains("-c:a:0 ac3 -b:a:0 640k -ac:a:0 6", c.ToCommandLine());
        Assert.Equal(new[] { "ac3" }, c.Expected.AudioCodecs);
    }

    [Fact]
    public void DtsAudio_WithAacChoice_BecomesAac384k()
    {
        var (c, _) = B(Hd264(audio: "dts"), Cfg(s => s.DtsOtherTarget = AudioFallback.Aac));
        Assert.Contains("-c:a:0 aac -b:a:0 384k -ac:a:0 6", c.ToCommandLine());
    }

    [Fact]
    public void CompatibleAudio_IsCopied_AndAacIsNotReencoded()
    {
        foreach (var a in new[] { "aac", "ac3", "eac3", "mp3" })
        {
            var (c, _) = B(Hd264(audio: a), Cfg());
            Assert.Contains("-c:a:0 copy", c.ToCommandLine());
        }
    }

    [Fact]
    public void Subtitles_AreStrippedByDefault_WithExplicitMaps()
    {
        var e = Ffp.Simple("S.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, extra: new[] { Ffp.Sub("subrip") });
        var (c, _) = B(e, Cfg());
        var line = c.ToCommandLine();
        Assert.Contains("-sn", line);
        Assert.DoesNotContain("-map 0:s", line);
        Assert.Contains("-dn", line);
        Assert.Equal(0, c.Expected.SubtitleStreams);
    }

    [Fact]
    public void KeptTextSubtitles_GoIntoMp4AsMovText()
    {
        var e = Ffp.Simple("S.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, extra: new[] { Ffp.Sub("subrip") });
        var (c, _) = B(e, Cfg(s => s.KeepEmbeddedSubsIfNoExternal = true));
        Assert.Contains("-map 0:s:0", c.ToCommandLine());
        Assert.Contains("-c:s mov_text", c.ToCommandLine());
        Assert.Equal("mp4", c.Extension);
    }

    [Fact]
    public void KeptImageSubtitles_ForceMkv()
    {
        var e = Ffp.Simple("S.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, extra: new[] { Ffp.Sub("hdmv_pgs_subtitle") });
        var (c, _) = B(e, Cfg(s => s.KeepEmbeddedSubsIfNoExternal = true));
        Assert.Equal("mkv", c.Extension);
        Assert.Contains("-c:s copy", c.ToCommandLine());
        Assert.DoesNotContain("faststart", c.ToCommandLine());
        Assert.Contains("-f matroska", c.ToCommandLine());
        Assert.Contains(c.Notes, n => n.Contains("MKV"));
    }

    [Fact]
    public void MkvContainerSetting_WritesMatroska()
    {
        var (c, _) = B(Hd264(), Cfg(s => s.Container = OutputContainer.Mkv));
        Assert.Equal("mkv", c.Extension);
        Assert.DoesNotContain("-tag:v hvc1", c.ToCommandLine());
        Assert.DoesNotContain("faststart", c.ToCommandLine());
    }

    [Fact]
    public void CleanMetadata_UsesExplicitMapsAndDropsChapterJunk()
    {
        var (c, _) = B(Hd264(), Cfg());
        var line = c.ToCommandLine();
        Assert.Contains("-map_metadata -1", line);
        Assert.Contains("+bitexact", line);
        Assert.DoesNotContain("-map 0 ", line);
    }

    [Fact]
    public void CleanOff_KeepsGlobalMetadata()
    {
        var (c, _) = B(Hd264(), Cfg(s => s.CleanJunkMetadata = false));
        Assert.Contains("-map_metadata 0", c.ToCommandLine());
        Assert.DoesNotContain("bitexact", c.ToCommandLine());
    }

    [Fact]
    public void DropChapters_Option()
    {
        var (c, _) = B(Hd264(), Cfg(s => s.DropChapters = true));
        Assert.Contains("-map_chapters -1", c.ToCommandLine());
        Assert.True(c.Expected.ChaptersDropped);
    }

    [Fact]
    public void Remux_CopiesVideo_AndTagsHevcForMp4()
    {
        var e = Ffp.Simple("H.mkv", Ffp.Mkv, "hevc", 1920, 1080, 3_000_000, "ac3", 6, 2_000_000_000, 6000,
            formatTags: new Dictionary<string, string> { ["comment"] = "Downloaded from www.yts.mx" });
        var (c, p) = B(e, Cfg(s => s.CleanMetadataOnSkipped = true));
        Assert.True(p.WritesFile);
        Assert.Contains("-c:v copy", c.ToCommandLine());
        Assert.Contains("-tag:v hvc1", c.ToCommandLine());
        Assert.True(c.Expected.VideoCopied);
    }

    [Fact]
    public void AudioLanguageFilter_KeepsOnlyChosen()
    {
        var streams = new[] { Ffp.Video("h264", 1920, 1080, 9_000_000), Ffp.Audio("ac3", 6, "eng"), Ffp.Audio("ac3", 6, "fre") };
        var e = Ffp.Entry("M.mkv", Ffp.Json(Ffp.Mkv, 6000, 9_500_000, streams), 5_000_000_000);
        var (c, _) = B(e, Cfg(s => { s.KeepOnlyLanguages = true; s.KeepLanguages = "eng"; }));
        Assert.Single(c.Expected.AudioCodecs);
        Assert.Contains("-map 0:a:0", c.ToCommandLine());
        Assert.DoesNotContain("-map 0:a:1", c.ToCommandLine());
    }

    [Fact]
    public void Arguments_WithSpecialCharacters_StayOneArgument()
    {
        var e = Hd264("It's [a] \"Test\" & more (2019) %1.mkv");
        var (c, _) = B(e, Cfg());
        Assert.Contains("D:\\It's [a] \"Test\" & more (2019) %1.mkv", c.Args);
    }
}

public class ProgressParserTests
{
    [Fact]
    public void Parses_Blocks_AndComputesFractionAndEta()
    {
        var p = new ProgressParser();
        Assert.Null(p.Feed("frame=100"));
        Assert.Null(p.Feed("fps=48.5"));
        Assert.Null(p.Feed("total_size=1048576"));
        Assert.Null(p.Feed("out_time_us=30000000"));
        Assert.Null(p.Feed("speed=2.0x"));
        var s = p.Feed("progress=continue");
        Assert.NotNull(s);
        Assert.Equal(30, s!.OutSeconds);
        Assert.Equal(2.0, s.Speed);
        Assert.Equal(48.5, s.Fps);
        Assert.Equal(0.25, s.Fraction(120), 3);
        Assert.Equal(TimeSpan.FromSeconds(45), s.Eta(120));
        Assert.False(s.End);
        Assert.True(p.Feed("progress=end")!.End);
    }

    [Fact]
    public void NaSpeed_GivesNoEta()
    {
        var p = new ProgressParser();
        p.Feed("out_time_us=1000000"); p.Feed("speed=N/A");
        var s = p.Feed("progress=continue")!;
        Assert.Null(s.Speed);
        Assert.Null(s.Eta(100));
    }
}

public class VerifierTests
{
    private static readonly Settings S = new();

    private static (ScanEntry E, PlanItem P, FfmpegCommand C) Setup(string codec = "h264", long size = 5_000_000_000)
    {
        var e = Ffp.Simple("M.mkv", Ffp.Mkv, codec, 1920, 1080, 9_000_000, "ac3", 6, size, 6000);
        var p = Planner.Plan(e, S.ToPlannerOptions());
        return (e, p, FfmpegCommandBuilder.Build(e, p, S, "x.part"));
    }

    private static ProbeInfo Out(string vcodec = "hevc", string acodec = "ac3", double dur = 6000, int w = 1920, int h = 1080, string lang = "eng") =>
        ProbeParser.Parse(Ffp.Json(Ffp.Mp4, dur, null, new[] { Ffp.Video(vcodec, w, h, 4_000_000), Ffp.Audio(acodec, 6, lang) }), null);

    [Fact]
    public void GoodOutput_Passes()
    {
        var (e, p, c) = Setup();
        var r = Verifier.Verify(e, p, c.Expected, Out(), 3_000_000_000, S);
        Assert.Equal(VerifyVerdict.Pass, r.Verdict);
        Assert.Equal(40, r.SavingPercent, 1);
    }

    [Fact]
    public void DurationOff_Fails()
    {
        var (e, p, c) = Setup();
        var r = Verifier.Verify(e, p, c.Expected, Out(dur: 5000), 3_000_000_000, S);
        Assert.Equal(VerifyVerdict.Fail, r.Verdict);
        Assert.Contains(r.Failures, f => f.StartsWith("duration"));
    }

    [Fact]
    public void WrongVideoCodec_Fails()
    {
        var (e, p, c) = Setup();
        Assert.Equal(VerifyVerdict.Fail, Verifier.Verify(e, p, c.Expected, Out(vcodec: "h264"), 3_000_000_000, S).Verdict);
    }

    [Fact]
    public void ResolutionChange_Fails()
    {
        var (e, p, c) = Setup();
        Assert.Equal(VerifyVerdict.Fail, Verifier.Verify(e, p, c.Expected, Out(w: 1280, h: 720), 3_000_000_000, S).Verdict);
    }

    [Fact]
    public void MissingAudio_Fails()
    {
        var (e, p, c) = Setup();
        var o = ProbeParser.Parse(Ffp.Json(Ffp.Mp4, 6000, null, new[] { Ffp.Video("hevc", 1920, 1080, 4_000_000) }), null);
        var r = Verifier.Verify(e, p, c.Expected, o, 3_000_000_000, S);
        Assert.Equal(VerifyVerdict.Fail, r.Verdict);
        Assert.Contains(r.Failures, f => f.Contains("audio"));
    }

    [Fact]
    public void WrongAudioLanguage_FailsWhenCleaned()
    {
        var (e, p, c) = Setup();
        Assert.Equal(VerifyVerdict.Fail, Verifier.Verify(e, p, c.Expected, Out(lang: "und"), 3_000_000_000, S).Verdict);
    }

    [Fact]
    public void TinyOutput_LooksTruncated()
    {
        var (e, p, c) = Setup();
        var r = Verifier.Verify(e, p, c.Expected, Out(), 50_000_000, S);
        Assert.Equal(VerifyVerdict.Fail, r.Verdict);
        Assert.Contains(r.Failures, f => f.Contains("truncated"));
    }

    [Fact]
    public void BiggerThanSource_Fails()
    {
        var (e, p, c) = Setup();
        Assert.Equal(VerifyVerdict.Fail, Verifier.Verify(e, p, c.Expected, Out(), 5_600_000_000, S).Verdict);
    }

    [Fact]
    public void KeepIfSmaller_Under15Percent_IsNotWorthIt()
    {
        var (e, p, c) = Setup();
        var r = Verifier.Verify(e, p, c.Expected, Out(), 4_500_000_000, S);   // 10 % saved, 477 MB
        Assert.Equal(VerifyVerdict.NotWorthIt, r.Verdict);
    }

    [Fact]
    public void KeepIfSmaller_Over15Percent_ButUnder100MB_IsNotWorthIt()
    {
        var (e, p, c) = Setup(size: 500_000_000);
        Assert.Equal(VerifyVerdict.NotWorthIt, Verifier.Verify(e, p, c.Expected, Out(), 410_000_000, S).Verdict);   // 18 %, 86 MB
        Assert.Equal(VerifyVerdict.Pass, Verifier.Verify(e, p, c.Expected, Out(), 380_000_000, S).Verdict);          // 24 %, 114 MB
    }

    [Fact]
    public void KeepIfSmaller_Limits_AreAdjustable()
    {
        var (e, p, c) = Setup();
        var s = new Settings { KeepIfSmallerPercent = 5, KeepIfSmallerMb = 10 };
        Assert.Equal(VerifyVerdict.Pass, Verifier.Verify(e, p, c.Expected, Out(), 4_500_000_000, s).Verdict);
    }

    [Fact]
    public void CompatReencode_IgnoresTheSavingsRule()
    {
        var e = Ffp.Simple("Old.avi", "avi", "mpeg4", 720, 576, 1_000_000, "mp3", 2, 700_000_000, 5000);
        var p = Planner.Plan(e, S.ToPlannerOptions());
        var c = FfmpegCommandBuilder.Build(e, p, S, "x.part");
        var o = ProbeParser.Parse(Ffp.Json(Ffp.Mp4, 5000, null, new[] { Ffp.Video("h264", 720, 576, 900_000), Ffp.Audio("mp3", 2) }), null);
        Assert.Equal(VerifyVerdict.Pass, Verifier.Verify(e, p, c.Expected, o, 690_000_000, S).Verdict);
    }

    [Fact]
    public void ProbeError_Fails()
    {
        var (e, p, c) = Setup();
        Assert.Equal(VerifyVerdict.Fail, Verifier.Verify(e, p, c.Expected, ProbeParser.Failed("broken"), 3_000_000_000, S).Verdict);
    }

    [Fact]
    public void JunkMetadataLeftBehind_Fails()
    {
        var (e, p, c) = Setup();
        var o = ProbeParser.Parse(Ffp.Json(Ffp.Mp4, 6000, null, new[] { Ffp.Video("hevc", 1920, 1080, 4_000_000), Ffp.Audio("ac3", 6) },
            new Dictionary<string, string> { ["comment"] = "from www.yts.mx" }), null);
        Assert.Equal(VerifyVerdict.Fail, Verifier.Verify(e, p, c.Expected, o, 3_000_000_000, S).Verdict);
    }
}

public class DecodeTestFilterTests
{
    [Fact]
    public void HarmlessTimestampNotes_AreIgnored_RealErrorsAreKept()
    {
        Assert.Equal("", ConversionEngine.RealDecodeErrors("[null @ 0000] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 356 >= 356\n[in#0 @ 0] Referenced QT chapter track not found"));
        Assert.Contains("Invalid data", ConversionEngine.RealDecodeErrors("[hevc @ 0] Invalid data found when processing input\n[null @ 0] Application provided invalid, non monotonically increasing dts"));
    }
}

public class ChapterFileTests
{
    [Fact]
    public void JunkTitles_AreRenamed_GoodTitlesKept()
    {
        var json = "{\"chapters\":[{\"start_time\":\"0.000000\",\"end_time\":\"60.5\",\"tags\":{\"title\":\"Opening\"}}," +
                   "{\"start_time\":\"60.5\",\"end_time\":\"120\",\"tags\":{\"title\":\"Downloaded from www.yts.mx\"}},{\"start_time\":\"120\",\"end_time\":\"130\"}]}";
        var ch = ChapterFile.ParseChapters(json);
        Assert.Equal(3, ch.Count);
        var text = ChapterFile.Build(ch, Settings.DefaultJunkPatterns);
        Assert.StartsWith(";FFMETADATA1", text);
        Assert.Contains("title=Opening", text);
        Assert.Contains("title=Chapter 02", text);
        Assert.Contains("title=Chapter 03", text);
        Assert.Contains("START=60500", text);
    }
}

public class SwapperAndUndoTests
{
    private static (Settings S, string Media, string Dir, RunLog Log, UndoJournal J) Env(Action<Settings>? f = null)
    {
        var dir = Fx.TempDir();
        var media = Path.Combine(dir, "Media", "Movies");
        Directory.CreateDirectory(media);
        var s = new Settings();
        f?.Invoke(s);
        return (s, media, dir, new RunLog(null), new UndoJournal(Path.Combine(dir, "undo.csv")));
    }

    private static QueueJob Job(Settings s, string media, string rel, string newExt = "mp4", long size = 1000, long newSize = 400)
    {
        var e = Fx.RealEntry(media, rel, size);
        var final = Path.Combine(Path.GetDirectoryName(e.FullPath)!, e.BaseName + "." + newExt);
        var part = final + ".convertr-part";
        using (var fs = File.Create(part)) fs.SetLength(newSize);
        return new QueueJob { Id = 1, Entry = e, FinalPath = final, PartPath = part, OutputBytes = newSize, HoldingPath = HoldingArea.HoldingPathFor(media, e.FullPath, s) };
    }

    [Fact]
    public void HoldingPath_MirrorsStructure_OutsideTheMediaFolder()
    {
        var s = new Settings();
        var media = Path.Combine("root", "Media", "Series");
        var p = HoldingArea.HoldingPathFor(media, Path.Combine(media, "Show", "S01", "E01.mkv"), s);
        Assert.Equal(Path.Combine("root", "Media", "_Convertr_Originals", "Series", "Show", "S01", "E01.mkv"), p);
    }

    [Fact]
    public void Swap_MovesOriginal_PlacesOutput_PreservesTimestamps_AndJournals()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, Path.Combine("Movie_F (1979)", "Movie_F (1979).mkv"));
        var mtime = File.GetLastWriteTimeUtc(job.Entry.FullPath);
        var ctime = File.GetCreationTimeUtc(job.Entry.FullPath);
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("run1", job, mtime, ctime, media);

        Assert.False(File.Exists(job.Entry.FullPath));
        Assert.True(File.Exists(job.HoldingPath));
        Assert.Equal(1000, new FileInfo(job.HoldingPath).Length);
        Assert.True(File.Exists(job.FinalPath));
        Assert.False(File.Exists(job.PartPath));
        Assert.EndsWith(".mp4", job.FinalPath);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(job.FinalPath));
        Assert.Equal(ctime, File.GetCreationTimeUtc(job.FinalPath));
        Assert.True(Swapper.TimestampMatches(job.FinalPath, mtime));
        var steps = UndoJournal.Read(j.Path!).Select(e => e.Step).ToArray();
        Assert.Equal(new[] { "Start", "OriginalMoved", "OutputPlaced", "Done" }, steps);
    }

    [Fact]
    public void Swap_WithTimestampsOff_LeavesTheNewFileTime()
    {
        var (s, media, dir, log, j) = Env(x => x.PreserveTimestamps = false);
        var job = Job(s, media, "M.mkv");
        var mtime = File.GetLastWriteTimeUtc(job.Entry.FullPath);
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, mtime, DateTime.MinValue, media);
        Assert.NotEqual(mtime, File.GetLastWriteTimeUtc(job.FinalPath));
    }

    [Fact]
    public void Swap_RefusesWhenFinalNameIsTaken_AndPutsTheOriginalBack()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, "M.mkv");
        File.WriteAllText(job.FinalPath, "someone else's file");
        var ex = Assert.Throws<SwapException>(() => new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, DateTime.UtcNow, DateTime.UtcNow, media));
        Assert.Contains("already taken", ex.Message);
        Assert.True(File.Exists(job.Entry.FullPath));
        Assert.False(File.Exists(job.HoldingPath));
        Assert.Equal("someone else's file", File.ReadAllText(job.FinalPath));
        Assert.Contains(UndoJournal.Read(j.Path!), e => e.Step == "RolledBack");
    }

    [Fact]
    public void Swap_SameExtension_ReplacesInPlace()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, "M.mp4", "mp4");
        Assert.Equal(job.Entry.FullPath, job.FinalPath);
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, File.GetLastWriteTimeUtc(job.Entry.FullPath), DateTime.MinValue, media);
        Assert.Equal(400, new FileInfo(job.FinalPath).Length);
        Assert.Equal(1000, new FileInfo(job.HoldingPath).Length);
    }

    [Fact]
    public void Swap_DeleteAfterVerify_IsRecordedAsNotUndoable()
    {
        var (s, media, dir, log, j) = Env(x => x.Originals = OriginalsHandling.DeleteAfterVerify);
        var job = Job(s, media, "M.mkv");
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, DateTime.UtcNow, DateTime.UtcNow, media);
        Assert.False(File.Exists(job.HoldingPath));
        var item = UndoService.Load(new[] { j.Path! }).Single();
        Assert.False(item.CanUndo);
        Assert.Contains("deleted", item.Status);
    }

    [Fact]
    public void Undo_RestoresOriginal_KeepsConvertedFile_RestoresTimestamps()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, Path.Combine("Show", "S01E01.mkv"));
        var mtime = File.GetLastWriteTimeUtc(job.Entry.FullPath);
        var ctime = File.GetCreationTimeUtc(job.Entry.FullPath);
        var orig = job.Entry.FullPath;
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, mtime, ctime, media);

        var items = UndoService.Load(new[] { j.Path! });
        Assert.Single(items);
        Assert.True(items[0].CanUndo);
        var undoJ = new UndoJournal(Path.Combine(dir, "undo2.csv"));
        var res = UndoService.Execute(items, undoJ, log);
        Assert.True(res.Single().Success);

        Assert.True(File.Exists(orig));
        Assert.Equal(1000, new FileInfo(orig).Length);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(orig));
        Assert.Equal(ctime, File.GetCreationTimeUtc(orig));
        Assert.False(File.Exists(job.FinalPath));                 // moved away, not deleted...
        var kept = Directory.GetFiles(Path.Combine(dir, "Media", HoldingArea.UndoneFolderName), "*", SearchOption.AllDirectories);
        Assert.Single(kept);                                      // ...it is in the undone folder
        Assert.Equal(400, new FileInfo(kept[0]).Length);
        Assert.Empty(UndoService.Load(new[] { j.Path! }));         // no longer offered
    }

    [Fact]
    public void Undo_WorksWhenTheConvertedFileHasTheSameNameAsTheOriginal()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, Path.Combine("Show", "S01E02.mp4"), newExt: "mp4");
        Assert.Equal(job.Entry.FullPath, job.FinalPath);
        var mtime = File.GetLastWriteTimeUtc(job.Entry.FullPath);
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, mtime, mtime, media);
        Assert.Equal(400, new FileInfo(job.FinalPath).Length);          // the new file is in place

        var items = UndoService.Load(new[] { j.Path! });
        Assert.True(items.Single().CanUndo, items.Single().Status);
        var res = UndoService.Execute(items, new UndoJournal(Path.Combine(dir, "undo3.csv")), log);
        Assert.True(res.Single().Success, res.Single().Message);
        Assert.Equal(1000, new FileInfo(job.Entry.FullPath).Length);    // the original is back
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(job.Entry.FullPath));
        var kept = Directory.GetFiles(Path.Combine(dir, "Media", HoldingArea.UndoneFolderName), "*", SearchOption.AllDirectories);
        Assert.Equal(400, new FileInfo(kept.Single()).Length);          // the converted file is kept, not deleted
    }

    [Fact]
    public void Undo_Refuses_WhenOriginalPlaceIsTaken_OrHoldingCopyIsGone()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, "A.mkv");
        new Swapper(s, j, log, 1, TimeSpan.Zero).Swap("r", job, DateTime.UtcNow, DateTime.UtcNow, media);
        File.WriteAllText(job.Entry.FullPath, "new file with the old name");
        var it = UndoService.Load(new[] { j.Path! }).Single();
        Assert.False(it.CanUndo);
        Assert.Contains("taken", it.Status);
        File.Delete(job.Entry.FullPath);
        File.Delete(job.HoldingPath);
        var it2 = UndoService.Load(new[] { j.Path! }).Single();
        Assert.False(it2.CanUndo);
        Assert.Contains("purged", it2.Status);
        var res = UndoService.Execute(new[] { it2 }, new UndoJournal(null), log);
        Assert.False(res.Single().Success);
        Assert.True(File.Exists(job.FinalPath));   // the converted file is never touched when undo is refused
    }

    [Fact]
    public void Recover_FinishesASwapThatCrashedAfterTheOriginalWasMoved()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, "A.mkv");
        var mtime = File.GetLastWriteTimeUtc(job.Entry.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(job.HoldingPath)!);
        File.Move(job.Entry.FullPath, job.HoldingPath);            // crash happened here
        Assert.True(new Swapper(s, j, log, 1, TimeSpan.Zero).Recover("r", job, mtime, DateTime.MinValue, media));
        Assert.True(File.Exists(job.FinalPath));
        Assert.False(File.Exists(job.PartPath));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(job.FinalPath));
    }

    [Fact]
    public void Recover_PutsTheOriginalBack_WhenThereIsNoOutput()
    {
        var (s, media, dir, log, j) = Env();
        var job = Job(s, media, "A.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(job.HoldingPath)!);
        File.Move(job.Entry.FullPath, job.HoldingPath);
        File.Delete(job.PartPath);
        Assert.False(new Swapper(s, j, log, 1, TimeSpan.Zero).Recover("r", job, DateTime.UtcNow, DateTime.MinValue, media));
        Assert.True(File.Exists(job.Entry.FullPath));
    }
}

public class QueueStoreTests
{
    [Fact]
    public void Queue_RoundTrips_WithStatesAndPlans()
    {
        var dir = Fx.TempDir();
        var e = Fx.RealEntry(dir, "M.mkv", 1000);
        var q = new QueueFile { RunId = "r1", MediaFolder = dir };
        q.Jobs.Add(new QueueJob { Id = 1, Entry = e, Plan = Planner.Plan(e, new PlannerOptions()), State = JobState.Encoding, FinalPath = "a.mp4", PartPath = "a.mp4.convertr-part" });
        q.Jobs.Add(new QueueJob { Id = 2, Entry = e, State = JobState.Done, OutputBytes = 5, SavingPercent = 33.5 });
        var path = Path.Combine(dir, "queue.json");
        QueueStore.Save(q, path);
        var back = QueueStore.Load(path)!;
        Assert.Equal("r1", back.RunId);
        Assert.Equal(2, back.Jobs.Count);
        Assert.Equal(JobState.Encoding, back.Jobs[0].State);
        Assert.Equal(PlanAction.ReencodeVideo, back.Jobs[0].Plan.Action);
        Assert.Equal("a.mp4.convertr-part", back.Jobs[0].PartPath);
        Assert.Equal(e.Probe.Video!.Codec, back.Jobs[0].Entry.Probe.Video!.Codec);
        Assert.Equal(1, back.Remaining);
        Assert.Equal(33.5, back.Jobs[1].SavingPercent);
    }

    [Fact]
    public void BrokenFile_LoadsAsNull_AndIsKept()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "queue.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(QueueStore.Load(path));
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Missing_LoadsAsNull() => Assert.Null(QueueStore.Load(Path.Combine(Fx.TempDir(), "none.json")));

    [Fact]
    public void Csv_RoundTripsQuotesCommasAndLineBreaks()
    {
        var line = Csv.Line(new[] { "a,b", "he said \"hi\"", "x\ny", "" });
        var rows = Csv.Parse(line + "\r\n" + Csv.Line(new[] { "1", "2", "3", "4" }) + "\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a,b", "he said \"hi\"", "x\ny", "" }, rows[0]);
    }
}

public class EngineTests
{
    private sealed class Rig
    {
        public Settings S = new() { Mode = EncoderMode.Cpu, KeepIfSmallerMb = 0 };
        public string Dir = Fx.TempDir();
        public string Media = "";
        public FakeFfmpeg Ff = new();
        public FakeProbe Probe = new();
        public RunLog Log = new(null);
        public List<string> LogLines = new();
        public UndoJournal Journal = null!;
        public string QueuePath = "";

        public Rig(Action<Settings>? f = null)
        {
            f?.Invoke(S);
            Media = Path.Combine(Dir, "Media", "Movies");
            Directory.CreateDirectory(Media);
            Journal = new UndoJournal(Path.Combine(Dir, "undo.csv"));
            QueuePath = Path.Combine(Dir, "queue.json");
            Log.Echo = l => { lock (LogLines) LogLines.Add(l); };
            Probe.JsonFor = p => Fx.OutputJson("hevc", "ac3", 600);
        }

        public ConversionEngine Engine(Func<string, long>? free = null) => new(S, Ff, Probe, Log, Journal, QueuePath, free, TimeSpan.Zero);

        public QueueFile Queue(params ScanEntry[] entries) =>
            QueueBuilder.Build(entries.Select(e => (e, Planner.Plan(e, S.ToPlannerOptions()))), S, Media, "run1");
    }

    [Fact]
    public async Task HappyPath_Encodes_Verifies_Swaps_AndReportsProgress()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "Movie_F (1979)\\Movie_F (1979).mkv".Replace('\\', Path.DirectorySeparatorChar), 2_000_000);
        var q = r.Queue(e);
        var eng = r.Engine();
        var seen = new List<JobState>();
        eng.JobChanged += j => seen.Add(j.State);
        var progress = new List<EngineProgress>();
        eng.ProgressChanged += p => progress.Add(p);
        var sum = await eng.RunAsync(q);

        Assert.Equal(1, sum.Done);
        var job = q.Jobs[0];
        Assert.Equal(JobState.Done, job.State);
        Assert.True(File.Exists(job.FinalPath));
        Assert.True(File.Exists(job.HoldingPath));
        Assert.False(File.Exists(e.FullPath));
        Assert.False(File.Exists(job.PartPath));
        Assert.Contains(JobState.Encoding, seen); Assert.Contains(JobState.Verifying, seen); Assert.Contains(JobState.Swapping, seen);
        Assert.Contains(progress, p => p.JobFraction > 0);
        Assert.Equal(400_000, new FileInfo(job.FinalPath).Length);
        Assert.Equal(80, job.SavingPercent, 1);
        Assert.Equal(e.Probe.Video != null ? new DateTime(e.MtimeUtcTicks, DateTimeKind.Utc) : default, File.GetLastWriteTimeUtc(job.FinalPath));
        Assert.Contains(r.LogLines, l => l.Contains("command: ffmpeg"));
        Assert.True(File.Exists(r.QueuePath));
        Assert.Equal(0, QueueStore.Load(r.QueuePath)!.Remaining);
    }

    [Fact]
    public async Task DryRun_TouchesNothing_AndLogsTheExactCommand()
    {
        var r = new Rig(s => s.DryRun = true);
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.Empty(r.Ff.Calls);
        Assert.True(File.Exists(e.FullPath));
        Assert.Equal(new[] { e.FullPath }, Directory.GetFiles(r.Media));
        Assert.Contains(r.LogLines, l => l.Contains("[dry run]") && l.Contains("-c:v libx265"));
        Assert.Equal(1, sum.Pending);
        Assert.False(File.Exists(r.QueuePath));
    }

    [Fact]
    public async Task OutputClash_IsRefused_NothingOverwritten()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        File.WriteAllText(Path.Combine(r.Media, "A.mp4"), "existing");
        var q = r.Queue(e);
        Assert.Equal(JobState.Skipped, q.Jobs[0].State);
        await r.Engine().RunAsync(q);
        Assert.Empty(r.Ff.Calls);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(r.Media, "A.mp4")));
        Assert.True(File.Exists(e.FullPath));
    }

    [Fact]
    public async Task TwoFilesWithTheSameFinalName_SecondIsSkipped()
    {
        var r = new Rig();
        var a = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var b = Fx.RealEntry(r.Media, "A.avi", 2_000_000);
        var q = r.Queue(a, b);
        Assert.Equal(JobState.Pending, q.Jobs[0].State);
        Assert.Equal(JobState.Skipped, q.Jobs[1].State);
    }

    [Fact]
    public async Task FfmpegCrash_LeavesOriginal_NoPartialFile_JobFailed()
    {
        var r = new Rig();
        r.Ff.ExitCode = 1; r.Ff.Stderr = "Error while decoding stream #0:0: Invalid data found";
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(1, sum.Failed);
        Assert.Equal(new[] { e.FullPath }, Directory.GetFiles(r.Media));
        Assert.Contains("Invalid data", q.Jobs[0].Message);
    }

    [Fact]
    public async Task FailedVerification_LeavesOriginal_AndDeletesTemp()
    {
        var r = new Rig();
        r.Probe.JsonFor = p => Fx.OutputJson("h264", "ac3", 600);   // wrong codec
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(1, sum.Failed);
        Assert.Equal(new[] { e.FullPath }, Directory.GetFiles(r.Media));
    }

    [Fact]
    public async Task DecodeTestFailure_FailsTheJob()
    {
        var r = new Rig();
        r.Ff.DecodeFails = true;
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(1, sum.Failed);
        Assert.Contains("decode", q.Jobs[0].Message);
        Assert.Equal(new[] { e.FullPath }, Directory.GetFiles(r.Media));
    }

    [Fact]
    public async Task NotSmallEnough_KeepsOriginal_DeletesOutput()
    {
        var r = new Rig();
        r.Ff.OutputSize = _ => 1_900_000;   // 5 % saved
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(1, sum.NotWorthIt);
        Assert.Equal(new[] { e.FullPath }, Directory.GetFiles(r.Media));
    }

    [Fact]
    public async Task Stop_KillsCurrentFile_DeletesPartial_KeepsRestInQueue()
    {
        var r = new Rig();
        r.Ff.Hang = true;
        var a = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var b = Fx.RealEntry(r.Media, "B.mkv", 2_000_000);
        var q = r.Queue(a, b);
        var eng = r.Engine();
        r.Ff.OnEncode = _ => Task.Run(async () => { await Task.Delay(100); eng.Stop(); });
        var sum = await eng.RunAsync(q);
        Assert.True(sum.Stopped);
        Assert.Equal(2, sum.Pending);
        Assert.Equal(2, Directory.GetFiles(r.Media).Length);
        Assert.All(Directory.GetFiles(r.Media), f => Assert.DoesNotContain("convertr-part", f));
        Assert.Equal(2, QueueStore.Load(r.QueuePath)!.Remaining);
    }

    [Fact]
    public async Task DriveFull_StopsTheRun_NothingChanged()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine(free: _ => 10_000).RunAsync(q);
        Assert.True(sum.Stopped);
        Assert.Contains("free space", sum.StopReason);
        Assert.Empty(r.Ff.Calls);
        Assert.Equal(JobState.Pending, q.Jobs[0].State);
    }

    [Fact]
    public async Task FfmpegReportsNoSpaceLeft_StopsCleanly()
    {
        var r = new Rig();
        r.Ff.ExitCode = 1; r.Ff.Stderr = "av_interleaved_write_frame(): No space left on device";
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.True(sum.Stopped);
        Assert.Equal(JobState.Pending, q.Jobs[0].State);
        Assert.Equal(new[] { e.FullPath }, Directory.GetFiles(r.Media));
    }

    [Fact]
    public async Task FileChangedSinceScan_IsSkipped()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        File.SetLastWriteTimeUtc(e.FullPath, DateTime.UtcNow);
        await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Skipped, q.Jobs[0].State);
        Assert.Empty(r.Ff.Calls);
    }

    [Fact]
    public async Task Resume_AfterRestart_RedoesEncodingJobs_AndSweepsLeftovers()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var q = r.Queue(e);
        q.Jobs[0].State = JobState.Encoding;
        File.WriteAllText(q.Jobs[0].PartPath, "half written");
        QueueStore.Save(q, r.QueuePath);
        var loaded = QueueStore.Load(r.QueuePath)!;
        var sum = await r.Engine().RunAsync(loaded);
        Assert.Equal(1, sum.Done);
        Assert.False(File.Exists(loaded.Jobs[0].PartPath));
    }

    [Fact]
    public async Task BatchLimit_StopsStartingNewFiles()
    {
        var r = new Rig(s => s.BatchLimitGb = 1);
        var a = Fx.RealEntry(r.Media, "A.mkv", 600_000);
        var b = Fx.RealEntry(r.Media, "B.mkv", 600_000);
        r.S.BatchLimitGb = 1;
        var q = r.Queue(a, b);
        // limit is in GB; tiny test files never reach it, so both run
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(2, sum.Done);
    }

    [Fact]
    public async Task Pause_HoldsBackTheNextFile_UntilResume()
    {
        var r = new Rig();
        var a = Fx.RealEntry(r.Media, "A.mkv", 2_000_000);
        var b = Fx.RealEntry(r.Media, "B.mkv", 2_000_000);
        var q = r.Queue(a, b);
        var eng = r.Engine();
        var first = new TaskCompletionSource();
        eng.JobChanged += j => { if (j.State == JobState.Done && !first.Task.IsCompleted) { eng.Pause(); first.SetResult(); } };
        var run = eng.RunAsync(q);
        await first.Task;
        await Task.Delay(300);
        Assert.False(run.IsCompleted);
        Assert.Equal(JobState.Pending, q.Jobs[1].State);
        eng.Resume();
        var sum = await run;
        Assert.Equal(2, sum.Done);
    }
}

public class RunWindowTests
{
    [Theory]
    [InlineData("02:00", true)]
    [InlineData("15:00", false)]
    [InlineData("00:30", false)]
    [InlineData("14:59", true)]
    public void SameDayWindow(string now, bool allowed) =>
        Assert.Equal(allowed, RunWindow.IsAllowed(DateTime.Today.Add(TimeSpan.Parse(now)), true, "01:00", "15:00"));

    [Theory]
    [InlineData("23:00", true)]
    [InlineData("05:59", true)]
    [InlineData("12:00", false)]
    public void WindowAcrossMidnight(string now, bool allowed) =>
        Assert.Equal(allowed, RunWindow.IsAllowed(DateTime.Today.Add(TimeSpan.Parse(now)), true, "22:00", "06:00"));

    [Fact]
    public void Disabled_AlwaysAllowed() => Assert.True(RunWindow.IsAllowed(DateTime.Today.AddHours(12), false, "01:00", "02:00"));

    [Fact]
    public async Task Engine_WaitsOutsideTheWindow_ThenRuns()
    {
        var dir = Fx.TempDir();
        var media = Path.Combine(dir, "Media"); Directory.CreateDirectory(media);
        var s = new Settings { Mode = EncoderMode.Cpu, KeepIfSmallerMb = 0, QuietHoursEnabled = true, QuietHoursStart = "01:00", QuietHoursEnd = "02:00" };
        var e = Fx.RealEntry(media, "A.mkv", 2_000_000);
        var ff = new FakeFfmpeg();
        var probe = new FakeProbe { JsonFor = _ => Fx.OutputJson("hevc", "ac3", 600) };
        var eng = new ConversionEngine(s, ff, probe, new RunLog(null), new UndoJournal(null), null, null, TimeSpan.Zero) { WindowPoll = TimeSpan.FromMilliseconds(20) };
        var clock = DateTime.Today.AddHours(12);
        eng.Now = () => clock;
        var statuses = new List<string>();
        eng.StatusChanged += statuses.Add;
        var q = QueueBuilder.Build(new[] { (e, Planner.Plan(e, s.ToPlannerOptions())) }, s, media, "r");
        var run = eng.RunAsync(q);
        await Task.Delay(150);
        Assert.False(run.IsCompleted);
        Assert.Empty(ff.Calls);
        Assert.NotEmpty(statuses);
        clock = DateTime.Today.AddHours(1).AddMinutes(30);
        var sum = await run;
        Assert.Equal(1, sum.Done);
    }
}
