// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Text.Json;
using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

/// <summary>The platform seams introduced for Convertr_svc (DESIGN.md section 2.4): Windows behaviour unchanged, Linux implementations testable anywhere.</summary>
public class PlatformTests
{
    private sealed class ScriptProbe : IHardwareProbe
    {
        private readonly Dictionary<string, string> _out;
        public ScriptProbe(Dictionary<string, string> o) { _out = o; }
        public Task<string> RunAsync(string exe, string[] args, CancellationToken ct) => Task.FromResult(_out.GetValueOrDefault(exe, ""));
    }

    [Fact]
    public void Windows_KeepsTodaysConventions()
    {
        var w = new WindowsPlatform();
        Assert.False(w.PathsCaseSensitive);
        Assert.Equal("ffmpeg.exe", w.ExeName("ffmpeg"));
        Assert.Equal(';', w.PathListSeparator);
        Assert.Empty(w.ExtraToolDirectories);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "settings.json"), w.Paths.SettingsPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ffmpeg"), w.Paths.FfmpegPreferredFolder);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convertr", "ffmpeg"), w.Paths.FfmpegFallbackFolder);
        Assert.True(w.FileTimes.CanSetCreationTime);
    }

    [Fact]
    public void Linux_Conventions()
    {
        var l = new LinuxPlatform();
        Assert.True(l.PathsCaseSensitive);
        Assert.Equal("ffmpeg", l.ExeName("ffmpeg"));
        Assert.Equal(':', l.PathListSeparator);
        Assert.Contains("/usr/lib/jellyfin-ffmpeg", l.ExtraToolDirectories);
        Assert.False(l.FileTimes.CanSetCreationTime);
        l.FileTimes.SetCreationUtc("/does/not/matter", DateTime.UtcNow);   // a no-op, never throws
    }

    [Fact]
    public void LinuxAppPaths_UseTheDataDirectory()
    {
        var d = Path.Combine(Path.DirectorySeparatorChar.ToString(), "var", "lib", "convertr-svc");   // the same text on both platforms
        var p = new LinuxAppPaths(d);
        Assert.Equal(d, p.DataDir);
        Assert.Equal(Path.Combine(d, "settings.json"), p.SettingsPath);
        Assert.Equal(Path.Combine(d, "queue.json"), p.QueuePath);
        Assert.Equal(Path.Combine(d, "probe-cache.json"), p.ProbeCachePath);
        Assert.Equal(Path.Combine(d, "logs"), p.LogsDir);
        Assert.Equal(Path.Combine(d, "ffmpeg"), p.FfmpegPreferredFolder);
    }

    [Fact]
    public void PathCompare_FollowsThePlatform()
    {
        var cs = PlatformServices.Current.PathsCaseSensitive;
        Assert.Equal(!cs, PathCompare.Equal("/a/Movie.mkv", "/a/movie.mkv"));
        Assert.True(PathCompare.Equal("/a/b.mkv", "/a/b.mkv"));
        var set = new HashSet<string>(PathCompare.Comparer) { "/a/B.mkv" };
        Assert.Equal(!cs, set.Contains("/a/b.mkv"));
        Assert.Equal(!cs, PathCompare.StartsWith("/A/b/c", "/a/b"));
    }

    [Fact]
    public void ParseCpuInfo_ReadsNameThreadsAndCores()
    {
        var text = "processor\t: 0\nmodel name\t: Intel(R) Core(TM)  i7-9700\nphysical id\t: 0\ncore id\t\t: 0\n\nprocessor\t: 1\nmodel name\t: Intel(R) Core(TM)  i7-9700\nphysical id\t: 0\ncore id\t\t: 1\n\nprocessor\t: 2\nmodel name\t: x\nphysical id\t: 0\ncore id\t\t: 0\n";
        var cpu = LinuxParsers.ParseCpuInfo(text);
        Assert.Equal("Intel(R) Core(TM) i7-9700", cpu.Name);
        Assert.Equal(3, cpu.LogicalThreads);
        Assert.Equal(2, cpu.PhysicalCores);
        Assert.Equal("", LinuxParsers.ParseCpuInfo("").Name);
    }

    [Fact]
    public async Task ParseLspci_FindsTheArcProB60_AndTheNvidiaCard_AndIgnoresOtherDevices()
    {
        var lspci = string.Join('\n',
            "00:02.0 Host bridge [0600]: Intel Corporation Device [8086:3e1f]",
            "03:00.0 VGA compatible controller [0300]: Intel Corporation Battlemage G21 [Arc Pro B60] [8086:e211]",
            "04:00.0 VGA compatible controller [0300]: NVIDIA Corporation GP102 [GeForce GTX 1080 Ti] [10de:1b06] (rev a1)",
            "04:00.1 Audio device [0403]: NVIDIA Corporation GP102 HDMI Audio Controller [10de:10ef]",
            "05:00.0 VGA compatible controller [0300]: Red Hat, Inc. QXL paravirtual graphic card [1b36:0100]");
        var gpus = LinuxParsers.ParseLspci(lspci);
        Assert.Equal(2, gpus.Count);
        Assert.Equal("Intel Arc Pro B60", gpus[0].Name);
        Assert.Equal("Intel", gpus[0].Vendor);
        Assert.Equal("NVIDIA GeForce GTX 1080 Ti", gpus[1].Name);
        Assert.Equal("NVIDIA", gpus[1].Vendor);

        var hw = new LinuxHardware("/does/not/exist");
        var merged = await hw.DetectGpusAsync(new ScriptProbe(new() { ["lspci"] = lspci, ["nvidia-smi"] = "NVIDIA GeForce GTX 1080 Ti, 551.23, 11264\n" }), default);
        Assert.Equal(2, merged.Count);
        Assert.Equal(11264, merged.Single(g => g.Vendor == "NVIDIA").VramMb);
        Assert.Equal("551.23", merged.Single(g => g.Vendor == "NVIDIA").Driver);
        Assert.NotNull(await hw.DetectCpuAsync(new ScriptProbe(new()), default));   // missing /proc/cpuinfo gives an empty CpuInfo, never an exception
    }

    [Fact]
    public void FfmpegEnvironment_AppliesPairsToTheProcess()
    {
        var env = new ListFfmpegEnvironment(new[] { "LIBVA_DRIVER_NAME=iHD", "LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu", "broken", "=x" });
        Assert.Equal(2, env.Pairs.Count);
        var psi = new ProcessStartInfo("ffmpeg");
        env.Apply(psi);
        Assert.Equal("iHD", psi.Environment["LIBVA_DRIVER_NAME"]);
        Assert.Equal("/usr/lib/x86_64-linux-gnu", psi.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal(3, FfmpegEnvironment.SystemLibsPairs.Length);

        var none = new ProcessStartInfo("ffmpeg");
        NoFfmpegEnvironment.Instance.Apply(none);
        Assert.Equal(Environment.GetEnvironmentVariable("LIBVA_DRIVER_NAME"), none.Environment.TryGetValue("LIBVA_DRIVER_NAME", out var v) ? v : null);   // nothing added
        Assert.IsType<NoFfmpegEnvironment>(FfmpegEnvironment.For(new Settings()));
        Assert.IsType<ListFfmpegEnvironment>(FfmpegEnvironment.For(new Settings { FfmpegEnvironment = { "A=b" } }));
    }

    [SkippableFact]
    public void LinuxProcessController_SuspendsAndResumesAProcess()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux only");
        using var p = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var c = new LinuxProcessController();
            Assert.True(c.CanSuspend);
            c.Suspend(p);
            Assert.True(WaitState(p.Id, 'T'), "process should be stopped");
            c.Resume(p);
            Assert.True(WaitState(p.Id, 'S'), "process should be running again");
        }
        finally { try { p.Kill(true); } catch { } }
    }

    private static bool WaitState(int pid, char want)
    {
        for (var i = 0; i < 50; i++)
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat[(stat.LastIndexOf(')') + 2)..][0];
            if (state == want || (want == 'S' && state is 'R' or 'S')) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [Fact]
    public void NewSettings_DefaultToTheDesktopBehaviour_AndOldFilesStillLoad()
    {
        var s = new Settings();
        Assert.Equal(GpuBackend.Nvenc, s.GpuBackend);
        Assert.Equal(GpuCodec.Hevc, s.GpuCodec);
        Assert.False(s.UseScratch);
        Assert.Empty(s.FfmpegEnvironment);
        Assert.False(s.FfmpegEnvironmentAuto);

        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ \"CpuCrf\": 22, \"MediaFolder\": \"\" }");   // a file from before these fields existed
        var loaded = SettingsStore.Load(path);
        Assert.Equal(22, loaded.CpuCrf);
        Assert.Equal(GpuBackend.Nvenc, loaded.GpuBackend);
        Assert.False(loaded.UseScratch);

        loaded.GpuBackend = GpuBackend.Vaapi; loaded.GpuCodec = GpuCodec.Av1; loaded.UseScratch = true; loaded.ScratchFolder = "/scratch";
        loaded.FfmpegEnvironment.AddRange(FfmpegEnvironment.SystemLibsPairs);
        SettingsStore.Save(loaded, path);
        var again = SettingsStore.Load(path);
        Assert.Equal(GpuBackend.Vaapi, again.GpuBackend);
        Assert.Equal(GpuCodec.Av1, again.GpuCodec);
        Assert.True(again.UseScratch);
        Assert.Equal("/scratch", again.ScratchFolder);
        Assert.Equal(3, again.FfmpegEnvironment.Count);
    }

    [Fact]
    public void InPlaceStorage_DoesExactlyWhatTheSwapperDoes()
    {
        var dir = Fx.TempDir();
        var media = Path.Combine(dir, "Media", "Movies");
        Directory.CreateDirectory(media);
        var s = new Settings();
        var journal = new UndoJournal(Path.Combine(dir, "undo.csv"));
        var e = Fx.RealEntry(media, "Movie_F (1979).mkv", 1000);
        var final = Path.Combine(media, "Movie_F (1979).mp4");
        var part = final + ".convertr-part";
        using (var fs = File.Create(part)) fs.SetLength(400);
        var job = new QueueJob { Id = 1, Entry = e, FinalPath = final, PartPath = part, OutputBytes = 400, HoldingPath = HoldingArea.HoldingPathFor(media, e.FullPath, s) };
        var mtime = File.GetLastWriteTimeUtc(e.FullPath);

        IJobStorage storage = new InPlaceStorage(new Swapper(s, journal, new RunLog(null), 1, TimeSpan.Zero));
        Assert.Equal("in-place", storage.Name);
        storage.Commit("r1", job, mtime, DateTime.MinValue, media);

        Assert.False(File.Exists(e.FullPath));
        Assert.True(File.Exists(job.HoldingPath));
        Assert.Equal(1000, new FileInfo(job.HoldingPath).Length);
        Assert.Equal(400, new FileInfo(final).Length);
        Assert.False(File.Exists(part));
        Assert.True(Swapper.TimestampMatches(final, mtime));
        Assert.Contains(UndoJournal.Read(Path.Combine(dir, "undo.csv")), x => x.Step == "Done");
    }
}
