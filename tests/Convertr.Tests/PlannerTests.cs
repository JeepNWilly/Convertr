// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class PlannerTests
{
    private static readonly PlannerOptions Defaults = new();

    private static PlanItem P(ScanEntry e, PlannerOptions? o = null) => Planner.Plan(e, o ?? Defaults);

    // ---------- Rule 1: errors ----------
    [Fact]
    public void Rule1_ProbeFailure_IsError()
    {
        var e = Ffp.Entry("Movie_C (2013) 1080p AAC.mp4", "", 2_000_000_000, stderr: "moov atom not found Invalid data found when processing input");
        var p = P(e);
        Assert.Equal(PlanAction.Error, p.Action);
        Assert.Equal(1, p.Rule);
        Assert.Contains("moov atom", p.Reason);
        Assert.False(p.WritesFile);
    }

    [Fact]
    public void Rule1_EmptyJsonObject_IsError()
    {
        var p = P(Ffp.Entry("a.mp4", "{}", stderr: "Invalid data found"));
        Assert.Equal(PlanAction.Error, p.Action);
    }

    [Fact]
    public void Rule1_GarbageOutput_IsError() => Assert.Equal(PlanAction.Error, P(Ffp.Entry("a.mkv", "not json at all")).Action);

    [Fact]
    public void Rule1_NoVideoStream_IsError()
    {
        var e = Ffp.Entry("music.mkv", Ffp.Json(Ffp.Mkv, 100, 128000, new[] { Ffp.Audio("aac", 2) }));
        var p = P(e);
        Assert.Equal(PlanAction.Error, p.Action);
        Assert.Contains("video", p.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule1_CoverArtOnly_IsNotAVideo()
    {
        var e = Ffp.Entry("music.mp4", Ffp.Json(Ffp.Mp4, 100, 128000, new[] { Ffp.Audio("aac", 2), Ffp.CoverArt() }));
        Assert.Equal(PlanAction.Error, P(e).Action);
    }

    [Fact]
    public void Rule1_MissingDuration_IsError()
    {
        var e = Ffp.Entry("x.mkv", Ffp.Json(Ffp.Mkv, null, 5_000_000, new[] { Ffp.Video("h264", 1920, 1080, 4_000_000), Ffp.Audio("aac", 2) }));
        var p = P(e);
        Assert.Equal(PlanAction.Error, p.Action);
        Assert.Contains("uration", p.Reason);
    }

    // ---------- Rule 2: HDR / 4K / interlaced guard ----------
    [Fact]
    public void Rule2_Hdr10_IsReview()
    {
        var e = Ffp.Entry("hdr.mkv", Ffp.Json(Ffp.Mkv, 6000, 20_000_000,
            new[] { Ffp.Video("hevc", 1920, 1080, 18_000_000, pix: "yuv420p10le", transfer: "smpte2084"), Ffp.Audio("eac3", 6) }));
        var p = P(e);
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Equal(2, p.Rule);
        Assert.Contains("HDR10", p.Reason);
    }

    [Fact]
    public void Rule2_DolbyVision_IsReview()
    {
        var e = Ffp.Entry("dv.mkv", Ffp.Json(Ffp.Mkv, 6000, 20_000_000,
            new[] { Ffp.Video("hevc", 1920, 1080, 18_000_000, pix: "yuv420p10le", dovi: true), Ffp.Audio("eac3", 6) }));
        var p = P(e);
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Contains("DolbyVision", p.Reason);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(3840, 1600)]
    [InlineData(4096, 2160)]
    public void Rule2_4K_IsReview(int w, int h)
    {
        var e = Ffp.Simple("uhd.mkv", Ffp.Mkv, "hevc", w, h, 30_000_000);
        var p = P(e);
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Contains("4K", p.Reason);
    }

    [Theory]
    [InlineData("tt")]
    [InlineData("bb")]
    [InlineData("tb")]
    [InlineData("bt")]
    public void Rule2_Interlaced_SwitchedOff_IsReview(string order)
    {
        var e = Ffp.Simple("tv.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, fieldOrder: order);
        var p = P(e, Defaults with { ProcessInterlaced = false });
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Equal(2, p.Rule);
        Assert.Contains("interlaced", p.Reason);
    }

    [Theory]
    [InlineData("tt")]
    [InlineData("bb")]
    [InlineData("tb")]
    [InlineData("bt")]
    public void Interlaced_AboveThreshold_IsReencodeWithDeinterlaceNote(string order)
    {
        var e = Ffp.Simple("tv.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, fieldOrder: order);
        var p = P(e);
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Equal(ReencodeKind.Size, p.Kind);
        Assert.Equal(5, p.Rule);
        Assert.True(p.Deinterlace);
        Assert.Contains("interlaced: deinterlace (bwdif) will be applied in conversion", p.Reason);
        Assert.True(p.EstSaveHighBytes > 0);
    }

    [Fact]
    public void Interlaced_BelowThreshold_IsSkipWithoutDeinterlace()
    {
        var e = Ffp.Simple("tv.mkv", Ffp.Mkv, "h264", 1920, 1080, 1_500_000, fieldOrder: "tt");
        var p = P(e);
        Assert.Equal(PlanAction.Skip, p.Action);
        Assert.False(p.Deinterlace);
    }

    [Fact]
    public void Interlaced_LegacyCodec_IsCompatReencodeWithDeinterlaceNote()
    {
        var e = Ffp.Simple("old.mpg", "mpeg", "mpeg2video", 720, 576, 4_000_000, "mp2", 2, fieldOrder: "tt");
        var p = P(e);
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Equal(ReencodeKind.Compat, p.Kind);
        Assert.True(p.Deinterlace);
        Assert.Contains("bwdif", p.Reason);
    }

    [Fact]
    public void Progressive_HasNoDeinterlace()
    {
        var e = Ffp.Simple("p.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, fieldOrder: "progressive");
        var p = P(e);
        Assert.False(p.Deinterlace);
        Assert.DoesNotContain("bwdif", p.Reason);
    }

    [Fact]
    public void Interlaced4K_StillReview_WhenProcessInterlacedOn()
    {
        var e = Ffp.Simple("uhd.mkv", Ffp.Mkv, "h264", 3840, 2160, 30_000_000, fieldOrder: "tt");
        var p = P(e);
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Contains("4K", p.Reason);
        Assert.DoesNotContain("interlaced", p.Reason);
    }

    [Fact]
    public void Rule2_OptIn_LetsHdrThrough()
    {
        var e = Ffp.Entry("hdr.mkv", Ffp.Json(Ffp.Mkv, 6000, 20_000_000,
            new[] { Ffp.Video("hevc", 1920, 1080, 18_000_000, pix: "yuv420p10le", transfer: "smpte2084"), Ffp.Audio("eac3", 6) }));
        var p = P(e, Defaults with { AllowHdr4k = true });
        Assert.Equal(PlanAction.Skip, p.Action);
        Assert.Equal(4, p.Rule);
    }

    [Fact]
    public void Rule2_UnknownFieldOrder_IsNotInterlaced()
    {
        var e = Ffp.Simple("old.avi", "avi", "mpeg4", 624, 352, 1_000_000, "mp3", 2, fieldOrder: "unknown");
        Assert.Equal(PlanAction.ReencodeVideo, P(e).Action);
    }

    // ---------- Rule 3: legacy ----------
    [Theory]
    [InlineData("avi", "mpeg4", "mp3", 2)]            // xvid/divx
    [InlineData("avi", "msmpeg4v3", "mp3", 2)]        // divx3
    [InlineData("asf", "vc1", "ac3", 6)]              // wmv
    [InlineData("mpeg", "mpeg1video", "mp2", 2)]      // mpg
    public void Rule3_LegacyCodecAndContainer_IsCompatReencode(string container, string codec, string audio, int ch)
    {
        var e = Ffp.Simple("Old Movie (1999).avi", container, codec, 624, 352, 900_000, audio, ch, sizeBytes: 700_000_000);
        var p = P(e);
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Equal(ReencodeKind.Compat, p.Kind);
        Assert.Equal(3, p.Rule);
        Assert.Equal("h264", p.TargetVideo);
        Assert.Equal(0, p.EstSaveHighBytes);   // keep-if-smaller does not apply, no savings promised
    }

    [Fact]
    public void Rule3_Xvid_InsideMkv_IsStillLegacy()
    {
        var e = Ffp.Simple("odd.mkv", Ffp.Mkv, "mpeg4", 720, 400, 1_500_000, "ac3", 6);
        var p = P(e);
        Assert.Equal(ReencodeKind.Compat, p.Kind);
    }

    [Fact]
    public void Rule3_WithMp2Audio_AlsoConvertsAudio()
    {
        var p = P(Ffp.Simple("Ali G.mpg", "mpeg", "mpeg1video", 352, 288, 1_100_000, "mp2", 2));
        Assert.True(p.AudioConversion);
        Assert.Contains("AAC", p.Reason);
    }

    [Fact]
    public void Rule3_Wmapro6ch_ConvertsToAc3()
    {
        var p = P(Ffp.Simple("Series_C S03E08.WMV", "asf", "wmv3", 640, 480, 1_000_000, "wmapro", 6));
        Assert.True(p.AudioConversion);
        Assert.Contains("AC3 640k", p.Reason);
    }

    [Fact]
    public void Rule3_AmbiguousChannelLayout_IsReview()
    {
        var p = P(Ffp.Simple("odd.avi", "avi", "mpeg4", 640, 360, 900_000, "mp2", 3));
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Contains("3 channels", p.Reason);
    }

    [Fact]
    public void Rule3_CompatibleAudioWithOddChannels_NeedsNoReview()
    {
        // 4-channel AC3 is copied as-is, so its layout does not matter.
        var p = P(Ffp.Simple("odd.avi", "avi", "mpeg4", 640, 360, 900_000, "ac3", 4));
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.False(p.AudioConversion);
    }

    [Fact]
    public void Rule3_UnknownChannelCount_IsReview()
    {
        var p = P(Ffp.Simple("odd.avi", "avi", "mpeg4", 640, 360, 900_000, "wmapro", 0));
        Assert.Equal(PlanAction.Review, p.Action);
    }

    // ---------- Rule 4: HEVC ----------
    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("yuv420p10le", 10)]
    public void Rule4_Hevc_IsSkipped(string pix, int bits)
    {
        var e = Ffp.Simple("Show S01E01.mkv", Ffp.Mkv, "hevc", 1920, 1080, 9_000_000, pix: pix);
        var p = P(e);
        Assert.Equal(PlanAction.Skip, p.Action);
        Assert.Equal(4, p.Rule);
        Assert.Contains(bits + "-bit", p.Reason);
    }

    [Fact]
    public void Rule4_Hevc_WithDtsOnly_IsAudioFix()
    {
        var p = P(Ffp.Simple("hevc-dts.mkv", Ffp.Mkv, "hevc", 1920, 1080, 6_000_000, "dts", 6));
        Assert.Equal(PlanAction.FixAudioOnly, p.Action);
        Assert.Equal(6, p.Rule);
    }

    // ---------- Rule 5: H.264 size candidates ----------
    [Fact]
    public void Rule5_H264_1080p_HighBitrate_IsSizeReencode()
    {
        var e = Ffp.Simple("Movie_D (2017).mkv", Ffp.Mkv, "h264", 1920, 1080, 11_100_000, sizeBytes: 9_000_000_000);
        var p = P(e);
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Equal(ReencodeKind.Size, p.Kind);
        Assert.Equal(5, p.Rule);
        Assert.Equal("hevc", p.TargetVideo);
        // 11.1 Mbps 1080p: the model expects a large saving and a range around it (exact numbers are in SavingsModelTests)
        Assert.InRange(p.EstSaveMidBytes, 3_000_000_000L, 6_000_000_000L);
        Assert.True(p.EstSaveLowBytes > 0 && p.EstSaveLowBytes < p.EstSaveMidBytes && p.EstSaveMidBytes < p.EstSaveHighBytes);
        Assert.False(p.LikelyRefused);
    }

    [Fact]
    public void Rule5_CpuMode_SavesMoreThanGpuMode()
    {
        var e = Ffp.Simple("x.mkv", Ffp.Mkv, "h264", 1920, 1080, 11_000_000, sizeBytes: 1_000_000_000);
        var p = P(e, Defaults with { Mode = EncoderMode.Cpu });
        var gpu = P(e, Defaults with { Mode = EncoderMode.Gpu });
        Assert.True(p.EstSaveMidBytes > gpu.EstSaveMidBytes);
        Assert.InRange(p.EstSaveMidBytes / 1e9, 0.5, 0.7);   // x265 CRF 21 keeps about 40% of an 11 Mbps 1080p video
    }

    [Theory]
    // (width, height, kbps, expected re-encode?)
    [InlineData(1920, 1080, 4000, true)]
    [InlineData(1920, 1080, 3999, false)]
    [InlineData(1920, 800, 4000, true)]     // scope 1080p is still the 1080p class
    [InlineData(1280, 720, 2500, true)]
    [InlineData(1280, 720, 2499, false)]
    [InlineData(1280, 536, 2500, true)]
    [InlineData(1280, 536, 1800, false)]
    [InlineData(1920, 1080, 2600, false)]   // 1080p needs 4000, not the 720p value
    [InlineData(1024, 576, 1500, true)]     // 576p
    [InlineData(624, 352, 1500, true)]      // 480p class by width
    [InlineData(624, 352, 1499, false)]
    [InlineData(512, 288, 1200, true)]      // below 480p
    [InlineData(512, 288, 1199, false)]
    public void Rule5_ThresholdPerResolution(int w, int h, int kbps, bool expected)
    {
        var e = Ffp.Simple("t.mkv", Ffp.Mkv, "h264", w, h, kbps * 1000L);
        var p = P(e);
        Assert.Equal(expected, p.Action == PlanAction.ReencodeVideo);
        Assert.Equal(expected ? 5 : 8, p.Rule);
    }

    [Fact]
    public void Rule5_ThresholdsComeFromOptions()
    {
        var e = Ffp.Simple("t.mkv", Ffp.Mkv, "h264", 1280, 720, 3_000_000);
        Assert.Equal(PlanAction.ReencodeVideo, P(e).Action);
        Assert.Equal(PlanAction.Skip, P(e, Defaults with { Kbps720 = 3500 }).Action);
    }

    [Fact]
    public void Rule5_EstimatedBitrate_IsMarkedInReason()
    {
        var e = Ffp.Entry("est.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_647_000, new[] { Ffp.Video("h264", 1920, 800), Ffp.Audio("ac3", 6, bitRate: 384000) }));
        Assert.Equal("estimated", e.Probe.Video!.KbpsSource);
        Assert.Equal(5263, e.Probe.Video.Kbps);
        var p = P(e);
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.Contains("~5,263", p.Reason);
    }

    [Fact]
    public void Rule5_UnknownBitrate_IsReview()
    {
        var e = Ffp.Entry("nobr.mkv", Ffp.Json(Ffp.Mkv, 6000, null, new[] { Ffp.Video("h264", 1920, 1080), Ffp.Audio("ac3", 6) }));
        var p = P(e);
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Contains("bitrate unknown", p.Reason);
    }

    [Fact]
    public void Rule5_TakesPriorityOverAudioFix_ButConvertsAudioToo()
    {
        var p = P(Ffp.Simple("Movie_E.mkv", Ffp.Mkv, "h264", 1280, 720, 3_900_000, "dts", 6));
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.True(p.AudioConversion);
        Assert.Contains("AC3 640k", p.Reason);
    }

    [Fact]
    public void Rule5_AmbiguousAudio_IsReview()
    {
        var p = P(Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1280, 720, 3_900_000, "dts", 5));
        Assert.Equal(PlanAction.Review, p.Action);
    }

    // ---------- Rule 6: audio only ----------
    [Theory]
    [InlineData("dts", 6, "AC3 640k")]
    [InlineData("dts", 2, "AAC 192k")]
    [InlineData("truehd", 8, "AC3 640k")]
    [InlineData("flac", 2, "AAC 192k")]
    [InlineData("pcm_s16le", 2, "AAC 192k")]
    [InlineData("opus", 2, "AAC 192k")]
    public void Rule6_NoCompatibleAudio_IsAudioFix(string codec, int ch, string target)
    {
        var p = P(Ffp.Simple("low.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, codec, ch));
        Assert.Equal(PlanAction.FixAudioOnly, p.Action);
        Assert.Equal(6, p.Rule);
        Assert.Contains(target, p.Reason);
        Assert.True(p.AudioConversion);
        Assert.Equal(0, p.EstSaveHighBytes);
    }

    [Fact]
    public void Rule6_DtsPlusAc3_IsNotFixed()
    {
        var e = Ffp.Entry("two.mkv", Ffp.Json(Ffp.Mkv, 6000, 2_000_000, new[]
            { Ffp.Video("h264", 1280, 720, 1_500_000), Ffp.Audio("dts", 6), Ffp.Audio("ac3", 6) }));
        Assert.Equal(PlanAction.Skip, P(e).Action);
    }

    [Fact]
    public void Rule6_SevenPointOne_Dts_IsConvertible()
    {
        var p = P(Ffp.Simple("x.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, "dts", 8));
        Assert.Equal(PlanAction.FixAudioOnly, p.Action);
        Assert.Contains("AC3 640k", p.Reason);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(0)]
    public void Rule6_AmbiguousChannelCounts_AreReview(int ch)
    {
        var p = P(Ffp.Simple("x.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, "dts", ch));
        Assert.Equal(PlanAction.Review, p.Action);
    }

    [Fact]
    public void Rule6_PreferAac_ChangesTarget()
    {
        var p = P(Ffp.Simple("x.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, "dts", 6), Defaults with { PreferAacForSurround = true });
        Assert.Contains("AAC 384k", p.Reason);
    }

    [Fact]
    public void NoAudioAtAll_IsNotAnAudioFix()
    {
        var e = Ffp.Entry("silent.mkv", Ffp.Json(Ffp.Mkv, 600, 1_000_000, new[] { Ffp.Video("h264", 1280, 720, 900_000) }));
        Assert.Equal(PlanAction.Skip, P(e).Action);
    }

    // ---------- Rule 7: remux ----------
    [Fact]
    public void Rule7_H264InAvi_IsRemux()
    {
        var p = P(Ffp.Simple("odd.avi", "avi", "h264", 1280, 720, 1_200_000));
        Assert.Equal(PlanAction.Remux, p.Action);
        Assert.Equal(7, p.Rule);
        Assert.Contains("AVI", p.Reason);
    }

    [Fact]
    public void Rule7_HevcInTransportStream_IsRemux()
    {
        var p = P(Ffp.Simple("rec.ts", "mpegts", "hevc", 1920, 1080, 8_000_000));
        Assert.Equal(PlanAction.Remux, p.Action);
    }

    [Fact]
    public void Rule7_CleanSkippedFiles_OnlyWhenOptedIn_AndJunkFound()
    {
        var junk = new Dictionary<string, string> { ["comment"] = "Downloaded from www.example.com" };
        var dirty = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, formatTags: junk);
        var clean = Ffp.Simple("b.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000);
        var on = Defaults with { CleanMetadataOnSkipped = true };
        Assert.Equal(PlanAction.Skip, P(dirty).Action);                 // default: off
        Assert.Equal(PlanAction.Remux, P(dirty, on).Action);
        Assert.Equal(PlanAction.Skip, P(clean, on).Action);             // nothing to clean
        Assert.Equal(PlanAction.Skip, P(dirty, on with { CleanJunkMetadata = false }).Action);
    }

    [Fact]
    public void Rule7_StripSubsOnSkipped_OnlyWhenOptedIn()
    {
        var e = Ffp.Simple("subs.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, extra: new[] { Ffp.Sub("subrip") });
        Assert.Equal(PlanAction.Skip, P(e).Action);
        Assert.Equal(PlanAction.Remux, P(e, Defaults with { StripSubsOnSkipped = true }).Action);
        Assert.Equal(PlanAction.Skip, P(Ffp.Simple("n.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000), Defaults with { StripSubsOnSkipped = true }).Action);
    }

    [Fact]
    public void Rule7_KeepEmbeddedWhenNoExternal_SuppressesSubtitleRemux()
    {
        var o = Defaults with { StripSubsOnSkipped = true, KeepEmbeddedSubsIfNoExternal = true };
        var noExt = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, extra: new[] { Ffp.Sub("subrip") });
        var withExt = Ffp.Simple("b.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, extra: new[] { Ffp.Sub("subrip") }, externalSubs: true);
        Assert.Equal(PlanAction.Skip, P(noExt, o).Action);
        Assert.Equal(PlanAction.Remux, P(withExt, o).Action);
    }

    // ---------- Rule 8 ----------
    [Fact]
    public void Rule8_EfficientH264_IsSkipped()
    {
        var p = P(Ffp.Simple("Sitcom S01E01.mkv", Ffp.Mkv, "h264", 1280, 720, 1_200_000, "aac", 2));
        Assert.Equal(PlanAction.Skip, p.Action);
        Assert.Equal(8, p.Rule);
        Assert.Contains("below 2,500", p.Reason);
    }

    [Fact]
    public void Rule8_Mp4H264_IsSkipped() =>
        Assert.Equal(PlanAction.Skip, P(Ffp.Simple("a.mp4", Ffp.Mp4, "h264", 1920, 1080, 2_000_000, "aac", 6)).Action);

    [Fact]
    public void UnknownCodec_IsReview()
    {
        var p = P(Ffp.Simple("a.mkv", Ffp.Mkv, "av1", 1920, 1080, 3_000_000, "aac", 6));
        Assert.Equal(PlanAction.Review, p.Action);
        Assert.Contains("av1", p.Reason);
    }

    // ---------- Subtitles ----------
    [Fact]
    public void Subtitles_PgsAreFlaggedAsImage_AndStripped()
    {
        var e = Ffp.Simple("bd.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000,
            extra: new[] { Ffp.Sub("hdmv_pgs_subtitle"), Ffp.Sub("subrip", "fre") });
        Assert.True(e.Probe.Subs[0].IsImage);
        Assert.False(e.Probe.Subs[1].IsImage);
        var p = P(e);
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.True(p.StripsSubs);
    }

    [Fact]
    public void Subtitles_KeepOption_OnlyWhenNoExternalFile()
    {
        var o = Defaults with { KeepEmbeddedSubsIfNoExternal = true };
        var noExt = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, extra: new[] { Ffp.Sub("subrip") });
        var ext = Ffp.Simple("b.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, extra: new[] { Ffp.Sub("subrip") }, externalSubs: true);
        Assert.False(P(noExt, o).StripsSubs);
        Assert.True(P(ext, o).StripsSubs);
        Assert.True(P(noExt).StripsSubs);   // default: strip everything
    }

    // ---------- Metadata findings ----------
    [Fact]
    public void Metadata_CoverArtAttachmentsAndCommentTag_AreJunk()
    {
        var e = Ffp.Simple("m.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000,
            extra: new[] { Ffp.CoverArt(), Ffp.Attachment("font.ttf"), Ffp.Attachment("font2.ttf"), Ffp.Data() },
            formatTags: new Dictionary<string, string> { ["comment"] = "Downloaded from UIndex.org", ["encoder"] = "libebml v1.4.2", ["creation_time"] = "2020-01-01" });
        var m = P(e).Metadata;
        Assert.Equal(2, m.JunkGlobalTags);
        Assert.Equal(2, m.Attachments);
        Assert.Equal(1, m.CoverArt);
        Assert.Equal(1, m.DataStreams);
        Assert.False(m.IsClean);
        Assert.Contains("2 attachments", m.Summary);
        Assert.StartsWith("junk:", m.Summary);
    }

    [Fact]
    public void Metadata_CleanFile_IsClean()
    {
        var m = P(Ffp.Simple("m.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000,
            formatTags: new Dictionary<string, string> { ["creation_time"] = "2020-01-01", ["major_brand"] = "isom" })).Metadata;
        Assert.True(m.IsClean);
        Assert.Equal("clean", m.Summary);
    }

    [Theory]
    [InlineData("Movie_E (2016)", "Movie_E (2016).mkv", false)]
    [InlineData("Movie_E", "Movie_E (2016).mkv", false)]
    [InlineData("Movie_E.2016.720p.YTS.BZ", "Movie_E (2016).mkv", true)]
    [InlineData("Totally Different Title", "Movie_E (2016).mkv", true)]
    [InlineData("www.torrentsite.com", "Movie_E (2016).mkv", true)]
    public void Metadata_TitleIsKeptOnlyIfItMatchesTheFileName(string title, string file, bool cleared)
    {
        var e = Ffp.Simple(file, Ffp.Mkv, "h264", 1920, 1080, 9_000_000, formatTags: new Dictionary<string, string> { ["title"] = title });
        Assert.Equal(cleared, P(e).Metadata.TitleCleared);
    }

    [Fact]
    public void Metadata_KeepSensibleTitleOff_ClearsAnyTitle()
    {
        var e = Ffp.Simple("Movie_E (2016).mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, formatTags: new Dictionary<string, string> { ["title"] = "Movie_E" });
        Assert.True(P(e, Defaults with { KeepSensibleTitle = false }).Metadata.TitleCleared);
    }

    [Fact]
    public void Metadata_JunkChapterTitles_AreCounted()
    {
        var e = Ffp.Simple("c.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, chapters: new[] { "Opening", "Visit www.example.com", null!, "Chapter 04" });
        Assert.Equal(2, P(e).Metadata.JunkChapters);
    }

    [Fact]
    public void Metadata_MkvmergeStatisticsTags_AreFound()
    {
        var e = Ffp.Entry("s.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[] { Ffp.Video("h264", 1280, 720, 1_000_000), Ffp.StatsAudio("ac3", 6) }));
        Assert.True(P(e).Metadata.StatsTags);
    }

    [Fact]
    public void Metadata_StreamTitleWithUrl_IsJunk_ButCommentaryIsNot()
    {
        var e = Ffp.Entry("t.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[]
        {
            Ffp.Video("h264", 1280, 720, 1_000_000),
            Ffp.Audio("ac3", 6, title: "Commentary by the director"),
            Ffp.Audio("ac3", 2, title: "Encoded by RARBG http://x.org"),
        }));
        Assert.Equal(1, P(e).Metadata.JunkStreamTags);
    }

    [Fact]
    public void Metadata_CustomJunkPatterns_AreUsed()
    {
        var e = Ffp.Simple("m.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, formatTags: new Dictionary<string, string> { ["artist"] = "SomeGroupXYZ" });
        Assert.True(P(e).Metadata.IsClean);
        Assert.Equal(1, P(e, Defaults with { JunkPatterns = new[] { "somegroupxyz" } }).Metadata.JunkGlobalTags);
    }

    [Fact]
    public void Metadata_InvalidRegexPattern_IsIgnored()
    {
        var e = Ffp.Simple("m.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000);
        var p = P(e, Defaults with { JunkPatterns = new[] { "(unclosed", "ok\\d" } });
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
    }

    // ---------- Misc ----------
    [Fact]
    public void Warnings_DoNotChangeTheAction_ButAreFlagged()
    {
        var p = P(Ffp.Simple("w.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, stderr: "[h264 @ 0000] SEI type 5 size 770 truncated"));
        Assert.Equal(PlanAction.ReencodeVideo, p.Action);
        Assert.True(p.HasWarnings);
    }

    [Fact]
    public void Planner_IsDeterministic_AndDoesNotMutateInput()
    {
        var e = Ffp.Simple("d.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000);
        var before = System.Text.Json.JsonSerializer.Serialize(e);
        var a = P(e);
        var b = P(e);
        Assert.Equal(a, b);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(e));
    }

    [Fact]
    public void Summary_CountsEveryAction()
    {
        var rows = new[]
        {
            Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, sizeBytes: 1_000_000_000),   // size
            Ffp.Simple("b.avi", "avi", "mpeg4", 640, 360, 900_000, sizeBytes: 500_000_000),          // compat
            Ffp.Simple("c.mkv", Ffp.Mkv, "h264", 1280, 720, 1_000_000, "dts", 6),                    // audio
            Ffp.Simple("d.mkv", Ffp.Mkv, "hevc", 1920, 1080, 5_000_000),                             // skip
            Ffp.Entry("e.mp4", ""),                                                                  // error
            Ffp.Simple("f.mkv", Ffp.Mkv, "hevc", 3840, 2160, 20_000_000),                            // review
            Ffp.Simple("g.avi", "avi", "h264", 1280, 720, 1_000_000),                                // remux
        };
        var s = PlanSummary.From(rows.Select(e => (e, P(e))));
        Assert.Equal(7, s.Total);
        Assert.Equal(1, s.ReencodeSize);
        Assert.Equal(1, s.ReencodeCompat);
        Assert.Equal(1, s.FixAudio);
        Assert.Equal(1, s.Skip);
        Assert.Equal(1, s.Error);
        Assert.Equal(1, s.Review);
        Assert.Equal(1, s.Remux);
        Assert.Equal(1_000_000_000, s.SizeCandidateBytes);
        Assert.True(s.EstSaveLowBytes > 0 && s.EstSaveLowBytes < s.EstSaveMidBytes && s.EstSaveMidBytes < s.EstSaveHighBytes);
        Assert.Equal(0, s.LikelyRefused);
    }

    [Theory]
    [InlineData(1920, 1080, ResolutionClass.P1080)]
    [InlineData(1920, 800, ResolutionClass.P1080)]
    [InlineData(1800, 1080, ResolutionClass.P1080)]
    [InlineData(1792, 816, ResolutionClass.P720)]
    [InlineData(1280, 720, ResolutionClass.P720)]
    [InlineData(1280, 480, ResolutionClass.P720)]
    [InlineData(1196, 720, ResolutionClass.P720)]
    [InlineData(1024, 576, ResolutionClass.P576)]
    [InlineData(720, 480, ResolutionClass.P480)]
    [InlineData(624, 352, ResolutionClass.P480)]
    [InlineData(592, 384, ResolutionClass.Below480)]
    [InlineData(320, 240, ResolutionClass.Below480)]
    [InlineData(3840, 2160, ResolutionClass.K4)]
    [InlineData(0, 0, ResolutionClass.Unknown)]
    public void ResolutionClassifier_MatchesTheLibraryScanBuckets(int w, int h, ResolutionClass expected) =>
        Assert.Equal(expected, ResolutionClassifier.Classify(w, h));
}
