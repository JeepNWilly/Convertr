// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class ParserTests
{
    [Fact]
    public void VideoBitrate_FromStream()
    {
        var e = Ffp.Simple("a.mp4", Ffp.Mp4, "h264", 1920, 1080, 2_249_000);
        Assert.Equal(2249, e.Probe.Video!.Kbps);
        Assert.Equal("stream", e.Probe.Video.KbpsSource);
    }

    [Fact]
    public void VideoBitrate_FromBpsTag()
    {
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 9_000_000, new[] { Ffp.Video("h264", 1280, 720, null, bps: "3912345"), Ffp.Audio("ac3", 6) }));
        Assert.Equal(3912, e.Probe.Video!.Kbps);
        Assert.Equal("tag", e.Probe.Video.KbpsSource);
    }

    [Fact]
    public void VideoBitrate_StaleBpsTagLargerThanTheWholeFile_IsIgnoredAndEstimated()
    {
        // Series_Q S01E06: BPS tag 7,012 kbps, but the whole file is 912 kbps (the tag is left over from the original rip)
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 1874, 912_000, new[] { Ffp.Video("h264", 1280, 720, null, bps: "7011929"), Ffp.Audio("aac", 2, bps: "128000") }));
        Assert.Equal("estimated", e.Probe.Video!.KbpsSource);
        Assert.InRange(e.Probe.Video.Kbps!.Value, 700, 800);
    }

    [Fact]
    public void VideoBitrate_BpsTagSlightlyAboveOverall_IsKept()
    {
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 4_000_000, new[] { Ffp.Video("h264", 1280, 720, null, bps: "4100000"), Ffp.Audio("aac", 2) }));
        Assert.Equal("tag", e.Probe.Video!.KbpsSource);
    }

    [Fact]
    public void VideoBitrate_EstimatedFromOverallMinusAudio_Using128kForUnknownAudio()
    {
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 2_000_000, new[] { Ffp.Video("h264", 1280, 720), Ffp.Audio("aac", 2) }));
        Assert.Equal("estimated", e.Probe.Video!.KbpsSource);
        Assert.Equal(1872, e.Probe.Video.Kbps);
    }

    [Fact]
    public void VideoBitrate_EstimatedUsesAudioBpsTagWhenPresent()
    {
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 2_000_000, new[] { Ffp.Video("h264", 1280, 720), Ffp.Audio("aac", 2, bps: "192000") }));
        Assert.Equal(1808, e.Probe.Video!.Kbps);
    }

    [Fact]
    public void VideoBitrate_NeverNegative()
    {
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 100_000, new[] { Ffp.Video("h264", 1280, 720), Ffp.Audio("aac", 2) }));
        Assert.Equal(0, e.Probe.Video!.Kbps);
    }

    [Fact]
    public void BitDepth_FromPixFmt_AndRawSample()
    {
        Assert.Equal(10, Ffp.Simple("a.mkv", Ffp.Mkv, "hevc", 1920, 1080, 5_000_000, pix: "yuv420p10le").Probe.Video!.BitDepth);
        Assert.Equal(8, Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 5_000_000).Probe.Video!.BitDepth);
        var e = Ffp.Entry("b.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[] { Ffp.Video("hevc", 1920, 1080, 4_000_000, pix: "yuv420p10le", bitsRaw: "10"), Ffp.Audio("aac", 2) }));
        Assert.Equal(10, e.Probe.Video!.BitDepth);
    }

    [Fact]
    public void FrameRate_IsParsedFromFraction() =>
        Assert.Equal(23.98, Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 5_000_000).Probe.Video!.Fps);

    [Fact]
    public void Hdr_Detection()
    {
        var hdr = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[] { Ffp.Video("hevc", 1920, 1080, 4_000_000, transfer: "smpte2084", dovi: true) }));
        Assert.Equal("HDR10+DolbyVision", hdr.Probe.Video!.Hdr);
        var hlg = Ffp.Entry("b.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[] { Ffp.Video("hevc", 1920, 1080, 4_000_000, transfer: "arib-std-b67") }));
        Assert.Equal("HLG", hlg.Probe.Video!.Hdr);
    }

    [Fact]
    public void AudioAndSubtitleDetails_AreCaptured()
    {
        var e = Ffp.Entry("a.mkv", Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[]
        {
            Ffp.Video("h264", 1920, 1080, 4_000_000),
            Ffp.Audio("eac3", 6, "eng", isDefault: true),
            Ffp.Audio("aac", 2, "fre"),
            Ffp.Sub("dvd_subtitle", "eng"),
            Ffp.Sub("subrip", "spa"),
        }));
        Assert.Equal(2, e.Probe.Audio.Count);
        Assert.Equal("eac3 5.1", e.Probe.Audio[0].Describe());
        Assert.True(e.Probe.Audio[0].IsDefault);
        Assert.Equal("fre", e.Probe.Audio[1].Language);
        Assert.Equal(2, e.Probe.Subs.Count);
        Assert.True(e.Probe.Subs[0].IsImage);
        Assert.Equal("eng eac3 5.1; fre aac 2.0", PlanCsv.Describe(e.Probe));
    }

    [Fact]
    public void ChapterTitles_AreCaptured()
    {
        var e = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 4_000_000, chapters: new[] { "One", "Two" });
        Assert.Equal(2, e.Probe.ChapterCount);
        Assert.Equal(new[] { "One", "Two" }, e.Probe.ChapterTitles);
    }

    [Fact]
    public void Warnings_AreCollapsedAndTruncated()
    {
        var e = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 4_000_000, stderr: "[h264 @ 1]   SEI   truncated\n[h264 @ 1] again\n" + new string('x', 500));
        Assert.StartsWith("[h264 @ 1] SEI truncated [h264 @ 1] again", e.Probe.Warnings);
        Assert.Equal(300, e.Probe.Warnings!.Length);
    }

    [Fact]
    public void FirstRealVideoStream_WinsOverCoverArtAndExtraVideo()
    {
        var e = Ffp.Entry("a.mp4", Ffp.Json(Ffp.Mp4, 6000, 5_000_000, new[]
            { Ffp.CoverArt(), Ffp.Video("hevc", 1920, 1080, 4_000_000), Ffp.Video("mjpeg", 100, 100, 1000), Ffp.Audio("aac", 2) }));
        Assert.Equal("hevc", e.Probe.Video!.Codec);
        Assert.Equal(1, e.Probe.AttachedPicCount);
        Assert.Equal(1, e.Probe.ExtraVideoStreams);
    }

    [Fact]
    public void SpecialCharactersInTags_AreHandled()
    {
        var e = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 4_000_000, formatTags: new Dictionary<string, string> { ["title"] = "Émilie \"Poulain\" & Co. [2001] #1 日本語" });
        Assert.Equal("Émilie \"Poulain\" & Co. [2001] #1 日本語", e.Probe.FormatTags["title"]);
    }

    [Fact]
    public void ProbeInfo_SurvivesJsonRoundTrip()
    {
        var e = Ffp.Simple("a.mkv", Ffp.Mkv, "h264", 1920, 1080, 4_000_000, extra: new[] { Ffp.Sub("subrip") }, chapters: new[] { "A" },
            formatTags: new Dictionary<string, string> { ["comment"] = "x" });
        var json = System.Text.Json.JsonSerializer.Serialize(e.Probe);
        var back = System.Text.Json.JsonSerializer.Deserialize<ProbeInfo>(json)!;
        Assert.Equal(e.Probe.Video!.Kbps, back.Video!.Kbps);
        Assert.Equal(e.Probe.Subs.Count, back.Subs.Count);
        Assert.Equal("x", back.FormatTags["comment"]);
        Assert.Equal(ResolutionClass.P1080, back.Video.Resolution);
    }
}
