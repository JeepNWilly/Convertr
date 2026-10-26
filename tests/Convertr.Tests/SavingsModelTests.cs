// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.


using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class SavingsModelTests
{
    private static PlannerOptions Opts(EncoderMode m) => new() { Mode = m };

    private static SavingsEstimate Est(long kbps, int w, int h, EncoderMode m, double seconds = 3600, PlannerOptions? o = null)
    {
        var size = (long)(kbps * 125.0 * seconds * 1.08);   // video plus a little audio
        return SavingsModel.Estimate(kbps, w, h, seconds, size, o ?? Opts(m));
    }

    // Measured on 30-90 s clips (docs/RESULTS_SAVINGS.md): source video kbps, size, x265 slow CRF 21 output kbps, NVENC p7 CQ 27 output kbps.
    private static readonly (string Name, int W, int H, double Src, double Cpu, double Gpu)[] Measured =
    {
        ("Series_J 720p", 1280, 640, 4246, 1450, 2156),
        ("Series_C S01E14 720p", 1280, 720, 3275, 1728, 2143),
        ("Series_C S10E16 1080p", 1920, 1080, 6597, 2501, 3908),
        ("Series_L 720p", 1280, 720, 6883, 1873, 2428),
        ("Series_C S10E13 720p", 1280, 718, 3761, 2233, 2500),
        ("Last Ship 1080p", 1920, 1080, 4963, 2787, 3647),
        ("Series_E 1080p", 1920, 1080, 10418, 5422, 5373),
        ("Series_G 720p", 1280, 720, 1816, 880, 0),
        ("The 100 720p", 1280, 720, 4888, 2307, 0),
    };

    [Fact]
    public void Model_MatchesTheMeasuredClips_OnAverage_AndNeverWildly()
    {
        double logCpu = 0, logGpu = 0; int nc = 0, ng = 0;
        foreach (var m in Measured)
        {
            var cpu = SavingsModel.OutputKbps(m.Src, m.W, m.H, EncoderMode.Cpu, 21);
            Assert.InRange(cpu / m.Cpu, 0.4, 2.6);
            logCpu += Math.Log(cpu / m.Cpu); nc++;
            if (m.Gpu > 0)
            {
                var gpu = SavingsModel.OutputKbps(m.Src, m.W, m.H, EncoderMode.Gpu, 27);
                Assert.InRange(gpu / m.Gpu, 0.5, 2.0);
                logGpu += Math.Log(gpu / m.Gpu); ng++;
            }
        }
        Assert.InRange(Math.Exp(logCpu / nc), 0.8, 1.25);
        Assert.InRange(Math.Exp(logGpu / ng), 0.8, 1.25);
    }

    [Fact]
    public void BiggerSources_SaveMore_InBothModes()
    {
        foreach (var mode in new[] { EncoderMode.Cpu, EncoderMode.Gpu })
        {
            double last = -1;
            foreach (var kbps in new long[] { 4200, 5500, 7000, 9000, 11000 })
            {
                var e = Est(kbps, 1920, 1080, mode);
                var frac = e.MidBytes / (kbps * 125.0 * 3600 * 1.08);
                Assert.True(frac >= last, $"{mode} {kbps}");
                last = frac;
            }
        }
    }

    [Fact]
    public void Cpu_SavesMoreThanGpu_ForTypicalFiles()
    {
        var cpu = Est(3500, 1280, 720, EncoderMode.Cpu);
        var gpu = Est(3500, 1280, 720, EncoderMode.Gpu);
        Assert.True(cpu.MidBytes > gpu.MidBytes * 1.2);
        Assert.InRange(cpu.MidBytes / (3500 * 125.0 * 3600 * 1.08), 0.40, 0.60);   // measured: x265 keeps 30-60% of a 720p 3.5 Mbps video
        Assert.InRange(gpu.MidBytes / (3500 * 125.0 * 3600 * 1.08), 0.20, 0.45);
    }

    [Fact]
    public void RangeIsOrdered_LowAtMostMidAtMostHigh()
    {
        foreach (var mode in new[] { EncoderMode.Cpu, EncoderMode.Gpu })
            foreach (var kbps in new long[] { 2600, 4100, 6000, 9000 })
            {
                var e = Est(kbps, 1280, 720, mode);
                Assert.True(e.LowBytes <= e.MidBytes && e.MidBytes <= e.HighBytes, $"{mode} {kbps}");
            }
    }

    [Fact]
    public void LeanFile_IsPredictedRefused_InGpuMode_ButNotCpu_AndSavesNothing()
    {
        var gpu = Est(4100, 1920, 1080, EncoderMode.Gpu);   // 1080p just above the 4,000 kbps threshold
        Assert.True(gpu.LikelyRefused);
        Assert.Equal(0, gpu.MidBytes);
        var cpu = Est(4100, 1920, 1080, EncoderMode.Cpu);
        Assert.False(cpu.LikelyRefused);
        Assert.True(cpu.MidBytes > 0);
    }

    [Fact]
    public void KeepRuleSwitchedOff_NoFileIsRefusedAsLongAsItShrinks()
    {
        var o = Opts(EncoderMode.Gpu) with { KeepPercent = 0, KeepMb = 0 };
        var e = Est(4100, 1920, 1080, EncoderMode.Gpu, o: o);
        Assert.False(e.LikelyRefused);
        Assert.True(e.MidBytes > 0);
    }

    [Theory]
    [InlineData(100_000_000L, 1_000_000_000L, 15, 100, false)]    // 10%: under the 15% rule
    [InlineData(150_000_000L, 1_000_000_000L, 15, 100, true)]     // 15% and 143 MB
    [InlineData(90_000_000L, 500_000_000L, 15, 100, false)]       // 18% but only 86 MB
    [InlineData(105_000_000L, 500_000_000L, 15, 100, true)]       // 21% and 100 MB
    [InlineData(0L, 500_000_000L, 0, 0, false)]                   // nothing saved is never a keep
    [InlineData(-5L, 500_000_000L, 0, 0, false)]
    public void WouldKeep_FollowsTheVerifierRule(long saved, long size, int pct, int mb, bool expected) =>
        Assert.Equal(expected, SavingsModel.WouldKeep(saved, size, pct, mb));

    // Intel Arc Pro B60 benchmark (5 movies, 30 s clips): size as % of the source. CPU x265 CRF 21 36.4; VAAPI q20 48.2, q23 29.6, q26 28.9, q29 18.0; QSV q21 30.3, q24 21.5, q27 18.2.
    [Fact]
    public void IntelModel_ReproducesTheMeasuredRatios_AtTheReferenceQualities()
    {
        var cpu = SavingsModel.OutputKbps(6000, 1920, 1080, EncoderMode.Cpu, 21);
        Assert.Equal(29.6 / 36.4, SavingsModel.IntelOutputKbps(6000, 1920, 1080, GpuBackend.Vaapi, 23) / cpu, 3);
        Assert.Equal(30.3 / 36.4, SavingsModel.IntelOutputKbps(6000, 1920, 1080, GpuBackend.Qsv, 21) / cpu, 3);
        // within a few points of the measured sizes of the other tested values (relative to the q23 / q21 anchors)
        var v23 = SavingsModel.IntelOutputKbps(6000, 1920, 1080, GpuBackend.Vaapi, 23);
        Assert.InRange(SavingsModel.IntelOutputKbps(6000, 1920, 1080, GpuBackend.Vaapi, 20) / v23, 48.2 / 29.6 * 0.7, 48.2 / 29.6 * 1.3);
        Assert.InRange(SavingsModel.IntelOutputKbps(6000, 1920, 1080, GpuBackend.Vaapi, 29) / v23, 18.0 / 29.6 * 0.7, 18.0 / 29.6 * 1.3);
        Assert.True(SavingsModel.IntelOutputKbps(6000, 1920, 1080, GpuBackend.Vaapi, 26) < v23);
    }

    [Fact]
    public void IntelModel_IsUsedOnlyForVaapiAndQsv_WindowsNvencEstimateUnchanged()
    {
        var nv = Opts(EncoderMode.Gpu);   // default backend Nvenc
        Assert.False(SavingsModel.UsesIntelModel(nv));
        Assert.False(SavingsModel.UsesIntelModel(nv with { Mode = EncoderMode.Cpu, GpuBackend = GpuBackend.Vaapi }));
        Assert.True(SavingsModel.UsesIntelModel(nv with { GpuBackend = GpuBackend.Vaapi }));
        Assert.True(SavingsModel.UsesIntelModel(nv with { GpuBackend = GpuBackend.Qsv }));
        var a = Est(8000, 1920, 1080, EncoderMode.Gpu);
        var b = Est(8000, 1920, 1080, EncoderMode.Gpu, o: nv with { GpuBackend = GpuBackend.Vaapi });
        Assert.NotEqual(a.MidBytes, b.MidBytes);
        Assert.True(SavingsModel.IntelEstimateMeasured(nv));
        Assert.False(SavingsModel.IntelEstimateMeasured(nv with { GpuCodec = GpuCodec.Av1 }));
    }

    [Fact]
    public void IntelEstimate_FollowsTheIntelQualityValue_AndTheBackend()
    {
        var o = Opts(EncoderMode.Gpu) with { GpuBackend = GpuBackend.Vaapi };
        var q23 = Est(8000, 1920, 1080, EncoderMode.Gpu, o: o).MidBytes;
        var q20 = Est(8000, 1920, 1080, EncoderMode.Gpu, o: o with { IntelCq = 20 }).MidBytes;
        var q29 = Est(8000, 1920, 1080, EncoderMode.Gpu, o: o with { IntelCq = 29 }).MidBytes;
        Assert.True(q20 < q23 && q23 < q29);
        var qsv = o with { GpuBackend = GpuBackend.Qsv };
        Assert.Equal(q23 > 0, Est(8000, 1920, 1080, EncoderMode.Gpu, o: qsv).MidBytes > 0);
        Assert.True(Est(8000, 1920, 1080, EncoderMode.Gpu, o: qsv with { IntelQsvCq = 27 }).MidBytes > Est(8000, 1920, 1080, EncoderMode.Gpu, o: qsv).MidBytes);
    }

    [Fact]
    public void QualitySteps_ScaleTheOutput()
    {
        var crf21 = SavingsModel.OutputKbps(4000, 1280, 720, EncoderMode.Cpu, 21);
        Assert.Equal(0.61, SavingsModel.OutputKbps(4000, 1280, 720, EncoderMode.Cpu, 24) / crf21, 3);
        Assert.Equal(1 / 0.61, SavingsModel.OutputKbps(4000, 1280, 720, EncoderMode.Cpu, 18) / crf21, 3);
        var cq27 = SavingsModel.OutputKbps(4000, 1280, 720, EncoderMode.Gpu, 27);
        Assert.Equal(0.64, SavingsModel.OutputKbps(4000, 1280, 720, EncoderMode.Gpu, 30) / cq27, 3);
        Assert.Equal(1 / 0.64, SavingsModel.OutputKbps(4000, 1280, 720, EncoderMode.Gpu, 24) / cq27, 3);
    }

    [Fact]
    public void ChangingTheQualitySetting_ChangesThePlannerEstimate()
    {
        var e = Fx.RealEntry(Fx.TempDir(), "q.mkv", 4_000_000_000, videoBps: 8_000_000, duration: 3600);
        try
        {
            var a = Planner.Plan(e, new Settings { Mode = EncoderMode.Cpu, CpuCrf = 21 }.ToPlannerOptions());
            var b = Planner.Plan(e, new Settings { Mode = EncoderMode.Cpu, CpuCrf = 25 }.ToPlannerOptions());
            Assert.True(b.EstSaveMidBytes > a.EstSaveMidBytes);
        }
        finally { }
    }

    [Fact]
    public void Summary_CountsLikelyRefused_AndMidSaving()
    {
        var lean = Ffp.Simple("lean.mkv", Ffp.Mkv, "h264", 1920, 1080, 4_100_000, sizeBytes: 1_900_000_000, duration: 3600);
        var fat = Ffp.Simple("fat.mkv", Ffp.Mkv, "h264", 1920, 1080, 10_000_000, sizeBytes: 4_600_000_000, duration: 3600);
        var o = new Settings { Mode = EncoderMode.Gpu }.ToPlannerOptions();
        var s = PlanSummary.From(new[] { lean, fat }.Select(e => (e, Planner.Plan(e, o))));
        Assert.Equal(2, s.ReencodeSize);
        Assert.Equal(1, s.LikelyRefused);
        Assert.Equal(1_900_000_000, s.LikelyRefusedBytes);
        Assert.True(s.EstSaveMidBytes > 1_500_000_000);
        Assert.True(s.EstSaveHighBytes > s.EstSaveMidBytes && s.EstSaveMidBytes > s.EstSaveLowBytes);
    }
}
