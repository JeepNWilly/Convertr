// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class IntelBackendTests
{
    private static Settings Cfg(Action<Settings>? f = null)
    {
        var s = new Settings { Mode = EncoderMode.Gpu, GpuBackend = GpuBackend.Vaapi, GpuDevice = "/dev/dri/renderD128" };
        f?.Invoke(s);
        return s;
    }

    private static FfmpegCommand B(ScanEntry e, Settings s)
    {
        var plan = Planner.Plan(e, s.ToPlannerOptions());
        return FfmpegCommandBuilder.Build(e, plan, s, "/scratch/out.convertr-part");
    }

    private static ScanEntry Hd264(string fo = "progressive") => Ffp.Simple("Movie (2019).mkv", Ffp.Mkv, "h264", 1920, 1080, 9_000_000, "ac3", 6, 5_000_000_000, 6000, fo);

    [Fact]
    public void Vaapi_Hevc_UsesIcqAndHwupload_InitBeforeInput()
    {
        var line = B(Hd264(), Cfg()).ToCommandLine();
        Assert.Contains("-y -init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va -i ", line);
        Assert.Contains("-vf format=nv12,hwupload -c:v hevc_vaapi -rc_mode ICQ -global_quality 23 -profile:v main -tag:v hvc1", line);
        Assert.DoesNotContain("nvenc", line);
    }

    [Fact]
    public void Qsv_Hevc_UsesLookAheadPreset()
    {
        var line = B(Hd264(), Cfg(s => s.GpuBackend = GpuBackend.Qsv)).ToCommandLine();
        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device qsv=qs@va -filter_hw_device qs", line);
        Assert.Contains("-vf format=nv12,hwupload=extra_hw_frames=64 -c:v hevc_qsv -preset veryslow -global_quality 21 -look_ahead 1 -look_ahead_depth 40", line);
    }

    [Fact]
    public void Intel_Defaults_AreTheMeasuredBenchmarkValues()
    {
        var s = new Settings();
        Assert.Equal(23, s.IntelCq);       // VAAPI HEVC ICQ q23: VMAF 95.7, 29.6 % of the source (Arc Pro B60, 5 movies, 30 s clips)
        Assert.Equal(21, s.IntelQsvCq);    // QSV q21 = the same picture quality and size as VAAPI q23
        Assert.Equal(27, s.IntelAv1Cq);    // untested placeholder
    }

    [Fact]
    public void Qsv_UsesItsOwnQuality_AndVaapiKeepsIntelCq()
    {
        var q = B(Hd264(), Cfg(s => { s.GpuBackend = GpuBackend.Qsv; s.IntelQsvCq = 22; s.IntelCq = 30; })).ToCommandLine();
        Assert.Contains("hevc_qsv -preset veryslow -global_quality 22 ", q);
        var v = B(Hd264(), Cfg(s => { s.IntelQsvCq = 10; s.IntelCq = 25; })).ToCommandLine();
        Assert.Contains("hevc_vaapi -rc_mode ICQ -global_quality 25 ", v);
    }

    [Fact]
    public void TenBit_Source_UsesP010_AndMain10()
    {
        var e = Hd264(); e.Probe.Video!.BitDepth = 10;
        var line = B(e, Cfg()).ToCommandLine();
        Assert.Contains("format=p010le,hwupload", line);
        Assert.Contains("-profile:v main10", line);
    }

    [Fact]
    public void Sd_Source_GetsOneLowerQuality()
    {
        var e = Ffp.Simple("Old.mkv", Ffp.Mkv, "h264", 720, 480, 2_000_000, "ac3", 2, 900_000_000, 5000);
        var line = B(e, Cfg()).ToCommandLine();
        Assert.Contains("-global_quality 22", line);
    }

    [Fact]
    public void Av1_IsOptIn_AndHasNoHvc1Tag()
    {
        var cmd = B(Hd264(), Cfg(s => { s.GpuCodec = GpuCodec.Av1; s.Container = OutputContainer.Mp4; }));
        var line = cmd.ToCommandLine();
        Assert.Contains("-c:v av1_vaapi -rc_mode CQP -global_quality 27", line);
        Assert.DoesNotContain("hvc1", line);
        Assert.DoesNotContain("-profile:v", line);
        Assert.Equal("av1", cmd.Expected.VideoCodec);
    }

    [Fact]
    public void Deinterlace_RunsOnTheCpuBeforeUpload()
    {
        var line = B(Hd264("tt"), Cfg()).ToCommandLine();
        Assert.Contains("-vf bwdif=mode=send_frame:parity=auto:deint=all,format=nv12,hwupload", line);
    }

    [Fact]
    public void LegacyH264Target_StaysOnTheCpuOnIntel()
    {
        var e = Ffp.Simple("Old.avi", "avi", "mpeg4", 720, 576, 2_000_000, "mp3", 2, 700_000_000, 5000);
        var cmd = B(e, Cfg());
        var line = cmd.ToCommandLine();
        Assert.Contains("-c:v libx264", line);
        Assert.DoesNotContain("init_hw_device", line);
        Assert.Contains(cmd.Notes, n => n.Contains("CPU"));
    }

    [Fact]
    public void ProbedEncoderArgs_Override_TheDefault()
    {
        var line = B(Hd264(), Cfg(s => s.GpuEncoderArgs = "-rc_mode CQP -qp {Q} -low_power 1")).ToCommandLine();
        Assert.Contains("-c:v hevc_vaapi -rc_mode CQP -qp 23 -low_power 1 -profile:v main", line);
    }

    [Fact]
    public void Nvenc_IsUnchanged_WhenBackendIsNvenc()
    {
        var line = B(Hd264(), Cfg(s => s.GpuBackend = GpuBackend.Nvenc)).ToCommandLine();
        Assert.Contains("-c:v hevc_nvenc -preset p7 -tune hq -rc vbr -cq 27", line);
        Assert.DoesNotContain("init_hw_device", line);
    }
}

public class IntelProbeTests
{
    private sealed class Fake : IIntelProcessRunner
    {
        public Func<string, IReadOnlyList<string>, IReadOnlyList<string>, (int, string)> Handler { get; set; } = (_, _, _) => (0, "");
        public List<(string Exe, IReadOnlyList<string> Args, IReadOnlyList<string> Env)> Calls { get; } = new();
        public Task<(int ExitCode, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyList<string> envPairs, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((exe, args, envPairs));
            return Task.FromResult(Handler(exe, args, envPairs));
        }
    }

    private static readonly FfmpegLocation Loc = new("/usr/lib/jellyfin-ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffprobe", "test");

    [Fact]
    public async Task NoRenderNode_GivesAReadableReason()
    {
        var f = new Fake();
        var caps = await IntelProbe.DetectAsync(Loc, new Settings { GpuDevice = "" }, f, deviceOverride: null);
        if (IntelHw.FirstRenderNode() == null)
        {
            Assert.False(caps.Any);
            Assert.Contains("render node", caps.Reason);
            Assert.Empty(f.Calls);
        }
    }

    [Fact]
    public async Task BundledLibsCrash_RetriesWithSystemLibs_AndRemembers()
    {
        var f = new Fake
        {
            Handler = (exe, args, env) =>
                exe.EndsWith("vainfo") ? (0, "vainfo: Driver version: Intel iHD driver 25.1") :
                env.Count == 0 ? (135, "Bus error") : (0, ""),
        };
        var s = new Settings { FfmpegEnvironmentAuto = true };
        var caps = await IntelProbe.DetectAsync(Loc, s, f, "/dev/dri/renderD128");
        Assert.True(caps.SystemLibsNeeded);
        Assert.Contains("LIBVA_DRIVER_NAME=iHD", caps.Environment);
        Assert.True(caps.VaapiHevc); Assert.True(caps.QsvHevc);
        Assert.Contains("iHD", caps.Driver);
        IntelProbe.Apply(s, caps);
        Assert.Contains("LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu", s.FfmpegEnvironment);
        Assert.Equal(GpuBackend.Vaapi, s.GpuBackend);
        Assert.Equal("/dev/dri/renderD128", s.GpuDevice);
        Assert.Contains("{Q}", s.GpuEncoderArgs);
    }

    [Fact]
    public async Task BundledLibsWork_NoEnvironmentIsAdded()
    {
        var caps = await IntelProbe.DetectAsync(Loc, new Settings { FfmpegEnvironmentAuto = true }, new Fake(), "/dev/dri/renderD128");
        Assert.False(caps.SystemLibsNeeded);
        Assert.Empty(caps.Environment);
    }

    [Fact]
    public async Task FirstCandidateRejected_NextOneIsUsed_AndAv1CanFail()
    {
        var f = new Fake
        {
            Handler = (exe, args, env) =>
            {
                if (exe.EndsWith("vainfo")) return (0, "");
                var line = string.Join(" ", args);
                if (line.Contains("av1_")) return (1, "Unknown encoder 'av1_vaapi'");
                if (line.Contains("hevc_vaapi") && line.Contains("-rc_mode ICQ")) return (1, "Unrecognized option 'rc_mode'");
                return (0, "");
            },
        };
        var caps = await IntelProbe.DetectAsync(Loc, new Settings(), f, "/dev/dri/renderD128");
        Assert.True(caps.VaapiHevc);
        Assert.Equal("-rc_mode CQP -qp {Q}", caps.VaapiHevcArgs);
        Assert.False(caps.VaapiAv1); Assert.False(caps.QsvAv1);
    }

    [Fact]
    public async Task PermissionDenied_SaysRenderGroup()
    {
        var f = new Fake { Handler = (exe, _, _) => exe.EndsWith("vainfo") ? (0, "") : (1, "Failed to open /dev/dri/renderD128: Permission denied") };
        var caps = await IntelProbe.DetectAsync(Loc, new Settings(), f, "/dev/dri/renderD128");
        Assert.False(caps.Any);
        Assert.Contains("render' group", caps.Reason);
    }

    [Theory]
    [InlineData(135, "", true)]
    [InlineData(1, "Bus error (core dumped)", true)]
    [InlineData(139, "", true)]
    [InlineData(1, "Unknown encoder", false)]
    [InlineData(0, "", false)]
    public void CrashDetection(int code, string err, bool crash) => Assert.Equal(crash, IntelProbe.IsCrash(code, err));
}

public class ScratchEngineTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class Rig
    {
        public Settings S = new() { Mode = EncoderMode.Cpu, KeepIfSmallerMb = 0, UseScratch = true };
        public string Dir = Fx.TempDir();
        public string Media = "";
        public string Scratch = "";
        public FakeFfmpeg Ff = new();
        public FakeProbe Probe = new();
        public RunLog Log = new(null);
        public UndoJournal Journal = null!;
        public string QueuePath = "";
        public ScratchStorage Storage = null!;
        public long Free = 100 * Gb;

        public Rig(Action<Settings>? f = null)
        {
            Media = Path.Combine(Dir, "Media", "Movies");
            Scratch = Path.Combine(Dir, "scratch");
            Directory.CreateDirectory(Media);
            S.ScratchFolder = Scratch;
            f?.Invoke(S);
            Journal = new UndoJournal(Path.Combine(Dir, "undo.csv"));
            QueuePath = Path.Combine(Dir, "queue.json");
            Probe.JsonFor = _ => Fx.OutputJson("hevc", "ac3", 600);
            Storage = new ScratchStorage(S, new Swapper(S, Journal, Log, 2, TimeSpan.Zero), Log, _ => Free);
        }

        public ConversionEngine Engine() => new(S, Ff, Probe, Log, Journal, QueuePath, _ => long.MaxValue, TimeSpan.Zero, Storage);

        public QueueFile Queue(params ScanEntry[] entries) =>
            QueueBuilder.Build(entries.Select(e => (e, Planner.Plan(e, S.ToPlannerOptions()))), S, Media, "run1");

        public string[] JobDirs() => Directory.Exists(Scratch) ? Directory.GetDirectories(Scratch, "job-*") : Array.Empty<string>();
    }

    [Fact]
    public async Task HappyPath_EncodesOnlyOnScratch_CopiesBackThenSwaps()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "Movie_F (1979)/Movie_F (1979).mkv", 2_000_000);
        var q = r.Queue(e);
        string? input = null, output = null;
        var libraryHadPartDuringEncode = false;
        r.Ff.OnEncode = args =>
        {
            input = args[args.ToList().IndexOf("-i") + 1];
            output = args[^1];
            libraryHadPartDuringEncode = Directory.GetFiles(r.Media, "*.convertr-part", SearchOption.AllDirectories).Length > 0;
        };
        var states = new List<JobState>();
        var eng = r.Engine();
        eng.JobChanged += j => states.Add(j.State);
        var sum = await eng.RunAsync(q);

        Assert.Equal(1, sum.Done);
        var job = q.Jobs[0];
        Assert.Equal(JobState.Done, job.State);
        Assert.StartsWith(r.Scratch, input); Assert.StartsWith(r.Scratch, output);
        Assert.NotEqual(e.FullPath, input);
        Assert.False(libraryHadPartDuringEncode);
        Assert.True(File.Exists(job.FinalPath));
        Assert.Equal(400_000, new FileInfo(job.FinalPath).Length);
        Assert.True(File.Exists(job.HoldingPath));
        Assert.False(File.Exists(e.FullPath));
        Assert.False(File.Exists(job.PartPath));
        Assert.Empty(r.JobDirs());
        Assert.Equal(0, r.Storage.ReservedBytes);
        Assert.Equal(new DateTime(e.MtimeUtcTicks, DateTimeKind.Utc), File.GetLastWriteTimeUtc(job.FinalPath));
        var order = states.Distinct().ToList();
        Assert.True(order.IndexOf(JobState.CopyingIn) < order.IndexOf(JobState.Encoding));
        Assert.True(order.IndexOf(JobState.Verifying) < order.IndexOf(JobState.CopyingBack));
        Assert.True(order.IndexOf(JobState.CopyingBack) < order.IndexOf(JobState.Swapping));
        Assert.Contains(HoldingArea.Root(r.Media, "_Convertr_Originals"), job.HoldingPath);
    }

    [Fact]
    public async Task NotSmaller_NothingIsCopiedBack_AndOriginalStays()
    {
        var r = new Rig(s => s.KeepIfSmallerMb = 0);
        r.Ff.OutputSize = _ => 1_900_000; // only 5 % smaller: below the keep-only-if-smaller limit
        var e = Fx.RealEntry(r.Media, "A (2000)/A (2000).mkv", 2_000_000);
        var q = r.Queue(e);
        var states = new List<JobState>();
        var eng = r.Engine();
        eng.JobChanged += j => states.Add(j.State);
        await eng.RunAsync(q);
        Assert.Equal(JobState.NotWorthIt, q.Jobs[0].State);
        Assert.DoesNotContain(JobState.CopyingBack, states);
        Assert.True(File.Exists(e.FullPath));
        Assert.Equal(2_000_000, new FileInfo(e.FullPath).Length);
        Assert.Empty(Directory.GetFiles(r.Media, "*.convertr-part", SearchOption.AllDirectories));
        Assert.Empty(r.JobDirs());
    }

    [Fact]
    public async Task FfmpegFailure_LeavesLibraryUntouched_AndCleansScratch()
    {
        var r = new Rig();
        r.Ff.ExitCode = 1; r.Ff.Stderr = "boom";
        var e = Fx.RealEntry(r.Media, "B (2001)/B (2001).mkv", 2_000_000);
        var q = r.Queue(e);
        await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Failed, q.Jobs[0].State);
        Assert.True(File.Exists(e.FullPath));
        Assert.Empty(r.JobDirs());
    }

    [Fact]
    public async Task KeepScratchOnFailure_KeepsAFolderForInspection()
    {
        var r = new Rig(s => s.KeepScratchOnFailure = true);
        r.Ff.ExitCode = 1; r.Ff.Stderr = "boom";
        var e = Fx.RealEntry(r.Media, "B (2001)/B (2001).mkv", 2_000_000);
        await r.Engine().RunAsync(r.Queue(e));
        Assert.Empty(r.JobDirs());
        Assert.Single(Directory.GetDirectories(r.Scratch, "kept-job-*"));
    }

    [Fact]
    public async Task NotEnoughScratchSpace_StopsTheRun_JobStaysPending()
    {
        var r = new Rig();
        r.Free = 1 * Gb; // auto budget = 0.9 - 2 GB = 0
        var e = Fx.RealEntry(r.Media, "C (2002)/C (2002).mkv", 2_000_000);
        var q = r.Queue(e);
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Pending, q.Jobs[0].State);
        Assert.True(sum.Stopped);
        Assert.Contains("scratch", q.Jobs[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.Ff.Calls);
        Assert.True(File.Exists(e.FullPath));
    }

    [Fact]
    public async Task PerFileCap_SkipsTheJob()
    {
        var r = new Rig(s => s.MaxScratchPerJobGb = 1);
        var e = Fx.RealEntry(r.Media, "D (2003)/D (2003).mkv", 2_000_000);
        // pretend the file is 3 GB
        e.SizeBytes = 3 * Gb;
        var q = r.Queue(e);
        await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Skipped, q.Jobs[0].State);
    }

    [Fact]
    public async Task LibraryOffline_KeepsJobPending_AndStopsTheRun()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "E (2004)/E (2004).mkv", 2_000_000);
        var q = r.Queue(e);
        Directory.Delete(r.Media, true); // the share went away
        var sum = await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Pending, q.Jobs[0].State);
        Assert.True(sum.Stopped);
        Assert.Contains("not reachable", q.Jobs[0].Message);
    }

    [Fact]
    public async Task UnmarkedNonEmptyScratchFolder_IsRefused()
    {
        var r = new Rig();
        Directory.CreateDirectory(r.Scratch);
        File.WriteAllText(Path.Combine(r.Scratch, "precious.txt"), "x");
        var e = Fx.RealEntry(r.Media, "F (2005)/F (2005).mkv", 2_000_000);
        var q = r.Queue(e);
        await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Failed, q.Jobs[0].State);
        Assert.Contains("not empty", q.Jobs[0].Message);
        Assert.True(File.Exists(Path.Combine(r.Scratch, "precious.txt")));
    }

    [Fact]
    public async Task CleanStale_RemovesOnlyJobFolders_OfTheMarkedScratchRoot()
    {
        var r = new Rig();
        r.Storage.EnsureRoot();
        var stale = Path.Combine(r.Scratch, "job-9-abc"); Directory.CreateDirectory(stale); File.WriteAllText(Path.Combine(stale, "source.mkv"), "x");
        var other = Path.Combine(r.Scratch, "mine"); Directory.CreateDirectory(other);
        var e = Fx.RealEntry(r.Media, "G (2006)/G (2006).mkv", 2_000_000);
        await r.Engine().RunAsync(r.Queue(e)); // run start cleans leftovers
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task ChecksumVerify_Works()
    {
        var r = new Rig(s => s.CopyBackVerify = CopyBackVerify.Checksum);
        var e = Fx.RealEntry(r.Media, "H (2007)/H (2007).mkv", 2_000_000);
        var q = r.Queue(e);
        await r.Engine().RunAsync(q);
        Assert.Equal(JobState.Done, q.Jobs[0].State);
    }

    [Fact]
    public async Task SourceChangedDuringCopyIn_FailsAndKeepsOriginal()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "I (2008)/I (2008).mkv", 2_000_000);
        var q = r.Queue(e);
        // simulate a change that happens after the scan matched but while copying: mtime moved by 10 s after the engine's first check is impossible to time here,
        // so verify the guard directly through the storage
        var job = q.Jobs[0];
        File.SetLastWriteTimeUtc(e.FullPath, DateTime.UtcNow);
        var ex = await Assert.ThrowsAsync<ScratchException>(() => r.Storage.StageAsync(job, r.Media, _ => { }, CancellationToken.None));
        Assert.Equal(ScratchProblem.Copy, ex.Problem);
        Assert.Empty(r.JobDirs());
        Assert.Equal(0, r.Storage.ReservedBytes);
    }

    [Fact]
    public async Task Recovery_CopyingBackJob_GoesBackToPending_AndPartIsRemoved()
    {
        var r = new Rig();
        var e = Fx.RealEntry(r.Media, "J (2009)/J (2009).mkv", 2_000_000);
        var q = r.Queue(e);
        var job = q.Jobs[0];
        job.State = JobState.CopyingBack;
        File.WriteAllText(job.PartPath, "half");
        QueueStore.Save(q, r.QueuePath);
        var q2 = QueueStore.Load(r.QueuePath)!;
        await r.Engine().RunAsync(q2);
        Assert.Equal(JobState.Done, q2.Jobs[0].State);
        Assert.False(File.Exists(q2.Jobs[0].PartPath));
    }

    [Fact]
    public void MountGuard_SameDirectory_IsSameMount()
    {
        var d = Fx.TempDir();
        Assert.True(MountGuard.SameMount(d, Path.Combine(d, "x", "y", "z.mkv")));
        Assert.True(MountGuard.SameMount(Path.Combine(d, "nonexistent", "a"), d));
    }

    [Fact]
    public void NeededBytes_IsTwiceTheSourcePlusMargin() => Assert.Equal(2L * 1000 + Gb / 2, ScratchStorage.NeededBytes(1000));
}
