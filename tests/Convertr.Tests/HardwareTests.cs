// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.


using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class HardwareParsingTests
{
    // Typical Get-CimInstance output (ConvertTo-Json -Compress) of a single-CPU desktop.
    private const string CpuJson = "{\"Name\":\"Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz\",\"NumberOfCores\":4,\"NumberOfLogicalProcessors\":8}";
    private const string GpuJson = "[{\"Name\":\"NVIDIA GeForce GTX 1080 Ti\",\"AdapterRAM\":4293918720,\"DriverVersion\":\"32.0.15.8180\"}," +
                                   "{\"Name\":\"Microsoft Basic Display Adapter\",\"AdapterRAM\":0,\"DriverVersion\":\"10.0.26100.1\"}]";
    private const string Smi = "NVIDIA GeForce GTX 1080 Ti, 581.80, 11264\r\n";

    [Fact]
    public void Cpu_SingleObject()
    {
        var c = HardwareParsers.ParseCpu(CpuJson);
        Assert.Equal("Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz", c.Name);
        Assert.Equal(4, c.PhysicalCores);
        Assert.Equal(8, c.LogicalThreads);
    }

    [Fact]
    public void Cpu_TwoSocketsAreAddedUp_AndGarbageIsEmpty()
    {
        var two = "[{\"Name\":\"Xeon  E5\",\"NumberOfCores\":8,\"NumberOfLogicalProcessors\":16},{\"Name\":\"Xeon E5\",\"NumberOfCores\":8,\"NumberOfLogicalProcessors\":16}]";
        var c = HardwareParsers.ParseCpu(two);
        Assert.Equal(16, c.PhysicalCores);
        Assert.Equal(32, c.LogicalThreads);
        Assert.Equal("Xeon E5", c.Name);
        Assert.Equal(0, HardwareParsers.ParseCpu("not json").LogicalThreads);
        Assert.Equal(0, HardwareParsers.ParseCpu("").LogicalThreads);
    }

    [Fact]
    public void Gpus_VirtualAdaptersSkipped_DriverConverted()
    {
        var g = HardwareParsers.ParseGpus(GpuJson);
        var one = Assert.Single(g);
        Assert.Equal("NVIDIA", one.Vendor);
        Assert.Equal("581.80", one.Driver);
        Assert.Equal("32.0.15.8180", one.WindowsDriver);
        Assert.Equal(0, one.VramMb);   // 32-bit AdapterRAM near 4 GB is not trusted
    }

    [Theory]
    [InlineData("32.0.15.8180", "581.80")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("30.0.14.9729", "497.29")]
    [InlineData("32.0.15.6094", "560.94")]
    
    public void NvidiaDriver_IsLastFiveDigits(string win, string expected) =>
        Assert.Equal(expected, HardwareParsers.NvidiaDriverFromWindows(win));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", "NVIDIA")]
    [InlineData("AMD Radeon RX 7800 XT", "AMD")]
    [InlineData("Intel(R) UHD Graphics 630", "Intel")]
    [InlineData("Intel(R) Arc(TM) A770", "Intel")]
    [InlineData("Matrox G200", "Other")]
    public void Vendor(string name, string vendor) => Assert.Equal(vendor, HardwareParsers.VendorOf(name));

    [Fact]
    public void NvidiaSmi_Csv()
    {
        var g = Assert.Single(HardwareParsers.ParseNvidiaSmi(Smi));
        Assert.Equal("NVIDIA GeForce GTX 1080 Ti", g.Name);
        Assert.Equal("581.80", g.Driver);
        Assert.Equal(11264, g.VramMb);
    }

    [Fact]
    public void NvidiaSmi_TwoCards_AndErrorText()
    {
        var two = HardwareParsers.ParseNvidiaSmi("NVIDIA GeForce RTX 4090, 560.94, 24564\nNVIDIA GeForce GTX 1060, 560.94, 6144\n");
        Assert.Equal(2, two.Count);
        Assert.Equal(6144, two[1].VramMb);
        Assert.Empty(HardwareParsers.ParseNvidiaSmi("'nvidia-smi' is not recognized as an internal or external command"));
        Assert.Empty(HardwareParsers.ParseNvidiaSmi("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));
        Assert.Empty(HardwareParsers.ParseNvidiaSmi(""));
    }

    [Fact]
    public void Merge_SmiSuppliesVramAndDriver()
    {
        var m = HardwareParsers.Merge(HardwareParsers.ParseGpus(GpuJson), HardwareParsers.ParseNvidiaSmi(Smi));
        var g = Assert.Single(m);
        Assert.Equal(11264, g.VramMb);
        Assert.Equal("581.80", g.Driver);
        Assert.Equal("32.0.15.8180", g.WindowsDriver);
    }

    [Fact]
    public void Merge_WithoutSmi_KeepsCim_AndKeepsOtherVendors()
    {
        var cim = HardwareParsers.ParseGpus("[{\"Name\":\"Intel(R) UHD Graphics 630\",\"AdapterRAM\":1073741824,\"DriverVersion\":\"31.0.101.2111\"},{\"Name\":\"NVIDIA GeForce RTX 3060\",\"AdapterRAM\":-1,\"DriverVersion\":\"31.0.15.5222\"}]");
        var m = HardwareParsers.Merge(cim, new());
        Assert.Equal(2, m.Count);
        Assert.Equal(1024, m[0].VramMb);
        Assert.Equal("552.22", m[1].Driver);
    }

    [Fact]
    public void Signature_ChangesWithGpuDriverOrFfmpeg_NotWithTime()
    {
        HardwareProfile P(string drv, string ff, DateTime t) => new()
            { DetectedUtc = t, FfmpegVersion = ff, Gpus = { new GpuInfo { Name = "NVIDIA GeForce GTX 1080 Ti", Driver = drv } } };
        var a = P("581.80", "8.0.1", DateTime.UtcNow).Signature();
        Assert.Equal(a, P("581.80", "8.0.1", DateTime.UtcNow.AddDays(9)).Signature());
        Assert.NotEqual(a, P("590.10", "8.0.1", DateTime.UtcNow).Signature());
        Assert.NotEqual(a, P("581.80", "8.1.2", DateTime.UtcNow).Signature());
    }

    [Fact]
    public void Profile_RoundTripsThroughSettingsJson()
    {
        var dir = Fx.TempDir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var s = new Settings();
            s.Hardware = new HardwareProfile { DetectedUtc = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc), Cpu = { Name = "X", PhysicalCores = 4, LogicalThreads = 8 }, Encoders = { Tested = true, NvencHevc = true } };
            s.UserModified.Add("GpuCq");
            SettingsStore.Save(s, path);
            var back = SettingsStore.Load(path);
            Assert.Equal(8, back.Hardware!.Cpu.LogicalThreads);
            Assert.True(back.Hardware.Encoders.NvencHevc);
            Assert.Equal(new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc), back.Hardware.DetectedUtc);
            Assert.Contains("GpuCq", back.UserModified);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class HardwareDefaultsTests
{
    private static HardwareProfile Hw(int threads, bool nvenc, bool two = true, string reason = "") => new()
    {
        Cpu = new CpuInfo { Name = "Test CPU", PhysicalCores = Math.Max(1, threads / 2), LogicalThreads = threads },
        Gpus = { new GpuInfo { Name = "NVIDIA GeForce GTX 1080 Ti", Vendor = "NVIDIA", Driver = "581.80", VramMb = 11264 } },
        Encoders = new EncoderTests { Tested = true, NvencHevc = nvenc, NvencH264 = nvenc, NvencTwoSessions = nvenc && two, NvencReason = reason },
    };

    [Fact]
    public void Auto_ManyThreads_NvencOk_ChoosesCpuSlow_ExplainsGpu()
    {
        var d = HardwareDefaults.Decide(Hw(8, true), DefaultsPolicy.Auto);
        Assert.Equal(EncoderMode.Cpu, d.Mode);
        Assert.Equal("slow", d.X265Preset);
        Assert.Equal(27, d.GpuCq);
        Assert.Equal(1, d.ParallelCpuJobs);
        Assert.Equal(2, d.ParallelGpuJobs);
        Assert.Contains("2 times smaller", d.Why);
        Assert.Contains("GPU is much faster", d.Why);
    }

    [Fact]
    public void Auto_FewThreads_NvencOk_ChoosesGpu_MediumPreset()
    {
        var d = HardwareDefaults.Decide(Hw(2, true), DefaultsPolicy.Auto);
        Assert.Equal(EncoderMode.Gpu, d.Mode);
        Assert.Equal("medium", d.X265Preset);
    }

    [Fact]
    public void Auto_FewThreads_NoNvenc_StaysCpu()
    {
        var d = HardwareDefaults.Decide(Hw(2, false, reason: "driver too old"), DefaultsPolicy.Auto);
        Assert.Equal(EncoderMode.Cpu, d.Mode);
        Assert.Equal("medium", d.X265Preset);
        Assert.Equal(1, d.ParallelGpuJobs);
        Assert.Contains("not available", d.Why);
    }

    [Theory]
    [InlineData(5, "medium")]
    [InlineData(6, "slow")]
    [InlineData(16, "slow")]
    public void PresetFollowsThreads(int threads, string preset) =>
        Assert.Equal(preset, HardwareDefaults.Decide(Hw(threads, false), DefaultsPolicy.Auto).X265Preset);

    [Fact]
    public void PreferGpu_UsesGpuWhenItWorks_ElseCpuWithReason()
    {
        Assert.Equal(EncoderMode.Gpu, HardwareDefaults.Decide(Hw(16, true), DefaultsPolicy.PreferGpuSpeed).Mode);
        var d = HardwareDefaults.Decide(Hw(16, false, reason: "needs a newer driver"), DefaultsPolicy.PreferGpuSpeed);
        Assert.Equal(EncoderMode.Cpu, d.Mode);
        Assert.Contains("NVENC is not available", d.Why);
    }

    [Fact]
    public void PreferCpu_AlwaysCpu() =>
        Assert.Equal(EncoderMode.Cpu, HardwareDefaults.Decide(Hw(2, true), DefaultsPolicy.PreferCpuQuality).Mode);

    [Fact]
    public void OneNvencSession_MeansOneGpuJob() =>
        Assert.Equal(1, HardwareDefaults.Decide(Hw(8, true, two: false), DefaultsPolicy.Auto).ParallelGpuJobs);

    [Fact]
    public void Apply_FirstLaunch_SetsEverything_AndStoresProfile()
    {
        var s = new Settings { Mode = EncoderMode.Gpu, GpuCq = 24, X265Preset = "fast", ParallelGpuJobs = 1 };
        var hw = Hw(8, true);
        var applied = HardwareDefaults.Apply(s, HardwareDefaults.Decide(hw, DefaultsPolicy.Auto), hw);
        Assert.Equal(EncoderMode.Cpu, s.Mode);
        Assert.Equal(27, s.GpuCq);
        Assert.Equal("slow", s.X265Preset);
        Assert.Equal(2, s.ParallelGpuJobs);
        Assert.Contains("Mode", applied);
        Assert.Same(hw, s.Hardware);
        Assert.False(string.IsNullOrEmpty(hw.DefaultsSummary));
        Assert.False(string.IsNullOrEmpty(hw.DefaultsWhy));
    }

    [Fact]
    public void Apply_PreservesUserModifiedSettings()
    {
        var s = new Settings { Mode = EncoderMode.Gpu, GpuCq = 22, X265Preset = "veryslow" };
        s.UserModified.Add(TunedSettings.Mode);
        s.UserModified.Add(TunedSettings.X265Preset);
        var hw = Hw(8, true);
        var applied = HardwareDefaults.Apply(s, HardwareDefaults.Decide(hw, DefaultsPolicy.Auto), hw);
        Assert.Equal(EncoderMode.Gpu, s.Mode);        // kept (user chose it)
        Assert.Equal("veryslow", s.X265Preset);       // kept
        Assert.Equal(27, s.GpuCq);                    // not modified by the user: updated
        Assert.DoesNotContain("Mode", applied);
        Assert.DoesNotContain("X265Preset", applied);
        Assert.Contains("kept your own choices", hw.DefaultsSummary);
    }

    [Fact]
    public void Redetect_KeepsFlags_ResetClearsThem()
    {
        var s = new Settings { Mode = EncoderMode.Gpu };
        s.UserModified.Add(TunedSettings.Mode);
        var hw = Hw(8, true);
        HardwareDefaults.Apply(s, HardwareDefaults.Decide(hw, DefaultsPolicy.Auto), hw);
        HardwareDefaults.Apply(s, HardwareDefaults.Decide(hw, DefaultsPolicy.Auto), hw);
        Assert.Contains("Mode", s.UserModified);
        Assert.Equal(EncoderMode.Gpu, s.Mode);
        HardwareDefaults.ApplyAll(s, HardwareDefaults.Decide(hw, DefaultsPolicy.Auto), hw);
        Assert.Empty(s.UserModified);
        Assert.Equal(EncoderMode.Cpu, s.Mode);
    }

    [Fact]
    public void MarkUserChanges_FlagsOnlyChangedTunedSettings()
    {
        var before = new Settings { Mode = EncoderMode.Cpu, GpuCq = 27 };
        var after = SettingsStore.Clone(before);
        after.Mode = EncoderMode.Gpu;
        after.CpuCrf = 19;   // not a hardware-tuned setting
        var marked = TunedSettings.MarkUserChanges(before, after);
        Assert.Equal(new[] { "Mode" }, marked);
        Assert.Contains("Mode", after.UserModified);
        Assert.DoesNotContain("GpuCq", after.UserModified);
        Assert.Empty(TunedSettings.MarkUserChanges(after, SettingsStore.Clone(after)));
    }

    [Fact]
    public void X265Threads_AddsPools_OnlyWhenSet()
    {
        var e = Fx.RealEntry(Fx.TempDir(), "a.mkv", 10);
        var s = new Settings { Mode = EncoderMode.Cpu };
        var plan = Planner.Plan(e, s.ToPlannerOptions());
        Assert.Contains("-x265-params aq-mode=3 ", FfmpegCommandBuilder.Build(e, plan, s, "o.part").ToCommandLine());
        s.X265Threads = 4;
        Assert.Contains("-x265-params aq-mode=3:pools=4 ", FfmpegCommandBuilder.Build(e, plan, s, "o.part").ToCommandLine());
    }
}

public class HardwareReportTests
{
    [Fact]
    public void Describe_ShowsNvencReasonAndDefaults()
    {
        var hw = new HardwareProfile
        {
            Cpu = new CpuInfo { Name = "CPU X", PhysicalCores = 4, LogicalThreads = 8 },
            Gpus = { new GpuInfo { Name = "NVIDIA GeForce GTX 1080 Ti", Vendor = "NVIDIA", Driver = "581.80", VramMb = 11264 } },
            FfmpegVersion = "9.0.2",
            Encoders = { Tested = true, NvencReason = "this ffmpeg needs NVENC API 13.1 but your NVIDIA driver provides 13.0. Update the driver, or use an older ffmpeg build (8.0.1 works), or use CPU mode." },
        };
        var d = HardwareDefaults.Decide(hw, DefaultsPolicy.Auto);
        HardwareDefaults.Apply(new Settings(), d, hw);
        var t = HardwareReport.Describe(hw);
        Assert.Contains("CPU: CPU X (4 cores, 8 threads)", t);
        Assert.Contains("11 GB, driver 581.80", t);
        Assert.Contains("not available: this ffmpeg needs NVENC API 13.1", t);
        Assert.Contains("8.0.1 works", t);
        Assert.Contains("Chosen defaults: CPU (x265)", t);
        Assert.Contains("Why:", t);
        hw.Encoders = new EncoderTests { Tested = true, NvencHevc = true, NvencH264 = true, NvencTwoSessions = true };
        Assert.Equal("works (HEVC and H.264; 2 sessions at once)", HardwareReport.NvencStatus(hw));
        Assert.StartsWith("Not detected", HardwareReport.Describe(null));
    }
}

public class SpeedEstimateTests
{
    [Fact]
    public void BenchmarkSpeed_IsPreferred_OverTypicalSpeed()
    {
        var s = new Settings { Mode = EncoderMode.Cpu, BenchmarkCpuX = 0.8, BenchmarkGpuX = 12 };
        Assert.Equal((0.8, true), SpeedEstimator.EncodeSpeed(s));
        s.Mode = EncoderMode.Gpu;
        Assert.Equal((12.0, true), SpeedEstimator.EncodeSpeed(s));
        s.BenchmarkGpuX = null;
        Assert.False(SpeedEstimator.EncodeSpeed(s).Measured);
    }

    [Fact]
    public void EstimateHours_ReencodeVsRemux()
    {
        var dir = Fx.TempDir();
        try
        {
            var s = new Settings { Mode = EncoderMode.Cpu, BenchmarkCpuX = 1.0 };
            var heavy = Fx.RealEntry(dir, "h.mkv", 10, codec: "h264");
            var plan = Planner.Plan(heavy, s.ToPlannerOptions());
            Assert.Equal(PlanAction.ReencodeVideo, plan.Action);
            var hours = SpeedEstimator.EstimateHours(new[] { (plan, 7200.0) }, s);
            Assert.Equal(2.0, hours, 3);
            s.BenchmarkCpuX = 2.0;
            Assert.Equal(1.0, SpeedEstimator.EstimateHours(new[] { (plan, 7200.0) }, s), 3);
            Assert.Equal(0, SpeedEstimator.EstimateHours(new[] { (plan, 0.0) }, s));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0.0, "under a minute")]
    [InlineData(0.5, "30 minutes")]
    [InlineData(2.0, "2 hours")]
    [InlineData(5.5, "5.5 hours")]
    [InlineData(72.0, "3 days")]
    public void FormatHours(double h, string text) => Assert.Equal(text, SpeedEstimator.Format(h));

    [Fact]
    public void BenchmarkArgs_SeekAndLimitBeforeInput_AndTempOutput()
    {
        var dir = Fx.TempDir();
        try
        {
            var s = new Settings { Mode = EncoderMode.Cpu };
            var e = Fx.RealEntry(dir, "b.mkv", 10, duration: 3600);
            var plan = Planner.Plan(e, s.ToPlannerOptions());
            var args = BenchmarkRunner.BuildArgs(e, plan, s, "T.mp4", 1200, 20);
            var i = args.IndexOf("-i");
            Assert.Equal(new[] { "-ss", "1200", "-t", "20" }, args.GetRange(i - 4, 4));
            Assert.Equal("T.mp4", args[^1]);
            Assert.DoesNotContain(args, a => a.EndsWith(".convertr-part"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
