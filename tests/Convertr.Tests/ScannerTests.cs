// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Text.Json;
using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

/// <summary>Fake ffprobe: returns canned JSON per file name and records what was probed.</summary>
public sealed class FakeRunner : IProbeRunner
{
    public ConcurrentBag<string> Probed { get; } = new();
    public int MaxConcurrent;
    private int _current;
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public Func<string, ProbeRunResult>? Respond { get; set; }
    public Action<string>? OnProbe { get; set; }

    public async Task<ProbeRunResult> RunAsync(string path, CancellationToken ct)
    {
        Probed.Add(path);
        var now = Interlocked.Increment(ref _current);
        int seen;
        do { seen = MaxConcurrent; } while (now > seen && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen);
        try
        {
            OnProbe?.Invoke(path);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            ct.ThrowIfCancellationRequested();
            if (Respond != null) return Respond(path);
            var json = Ffp.Json(Ffp.Mkv, 6000, 5_000_000, new[] { Ffp.Video("h264", 1920, 1080, 4_500_000), Ffp.Audio("ac3", 6) });
            return new ProbeRunResult(json, "", 0, false);
        }
        finally { Interlocked.Decrement(ref _current); }
    }
}

public class ScannerTests
{
    private static string Snapshot(string root) =>
        string.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));

    [Fact]
    public async Task FindsVideos_IgnoresOthers_AndHandlesSpecialCharacters()
    {
        using var t = new TempDir();
        t.File("Movies/Émilie (2001) [1080p] #1 & 'Co'.mkv");
        t.File("Movies/Plain Movie (2020).mp4");
        t.File("Movies/Plain Movie (2020).en.srt");
        t.File("Movies/Other Movie (2019).mkv");
        t.File("Movies/Movie 100% $HOME `x` ;rm -rf.mkv");
        t.File("Movies/日本語 映画 (2010).avi");
        t.File("Movies/readme.txt");
        t.File("Movies/poster.jpg");
        t.File("Movies/Sub/Deep Folder/Episode S01E01.m4v");
        t.File("Movies/Plex Versions/Optimized/Dup.mkv");
        t.File("Movies/@eaDir/thumb.mkv");
        t.File("Movies/_Convertr_Originals/Old.mkv");
        var runner = new FakeRunner();
        var scanner = new Scanner(runner, new ProbeCache(null));
        var res = await scanner.ScanAsync(new[] { new LibraryRoot("Movies", Path.Combine(t.Path, "Movies")) }, null, CancellationToken.None);

        Assert.False(res.Cancelled);
        Assert.Equal(6, res.Entries.Count);
        Assert.Equal(6, res.Probed);
        var names = res.Entries.Select(e => e.RelativePath.Replace('\\', '/')).ToList();
        Assert.Contains("Émilie (2001) [1080p] #1 & 'Co'.mkv", names);
        Assert.Contains("Movie 100% $HOME `x` ;rm -rf.mkv", names);
        Assert.Contains("日本語 映画 (2010).avi", names);
        Assert.Contains("Sub/Deep Folder/Episode S01E01.m4v", names);
        Assert.DoesNotContain(names, n => n.Contains("Plex Versions") || n.Contains("@eaDir") || n.Contains("_Convertr_Originals"));
        Assert.All(res.Entries, e => Assert.Equal("Movies", e.Library));
        Assert.All(res.Entries, e => Assert.True(File.Exists(e.FullPath)));
        // the exact paths (special characters intact) reached the probe runner
        Assert.Contains(runner.Probed, p => p.EndsWith("Émilie (2001) [1080p] #1 & 'Co'.mkv"));
    }

    [Fact]
    public async Task ExternalSubtitles_AreDetectedByBaseName()
    {
        using var t = new TempDir();
        t.File("L/Plain Movie (2020).mp4");
        t.File("L/Plain Movie (2020).en.srt");
        t.File("L/Other Movie (2019).mkv");
        t.File("L/Plain Movie (2020) Extended.mkv");
        t.File("L/S/Show S01E01.mkv");
        t.File("L/S/Show S01E01.idx");
        var res = await new Scanner(new FakeRunner(), new ProbeCache(null)).ScanAsync(new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) }, null, CancellationToken.None);
        bool Has(string n) => res.Entries.Single(e => e.FileName == n).HasExternalSubs;
        Assert.True(Has("Plain Movie (2020).mp4"));
        Assert.False(Has("Other Movie (2019).mkv"));
        Assert.False(Has("Plain Movie (2020) Extended.mkv"));
        Assert.True(Has("Show S01E01.mkv"));
    }

    [Fact]
    public async Task Scan_NeverModifiesTheLibrary()
    {
        using var t = new TempDir();
        t.File("L/a.mkv", "aaaa"); t.File("L/b/c.mp4", "bb"); t.File("L/b/c.srt", "s");
        var root = Path.Combine(t.Path, "L");
        var before = Snapshot(root);
        var cachePath = Path.Combine(t.Path, "cache", "probe_cache.json");
        var res = await new Scanner(new FakeRunner(), new ProbeCache(cachePath)).ScanAsync(new[] { new LibraryRoot("L", root) }, null, CancellationToken.None);
        Assert.Equal(2, res.Entries.Count);
        Assert.Equal(before, Snapshot(root));        // no new, changed, renamed or deleted files and no mtime changes
        Assert.True(File.Exists(cachePath));         // the cache lives outside the library
    }

    [Fact]
    public async Task SecondScan_UsesTheCache_AndChangedFilesAreProbedAgain()
    {
        using var t = new TempDir();
        t.File("L/a.mkv", "aaaa"); t.File("L/b.mkv", "bb");
        var root = new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) };
        var cachePath = Path.Combine(t.Path, "probe_cache.json");

        var r1 = new FakeRunner();
        await new Scanner(r1, ProbeCache.Load(cachePath)).ScanAsync(root, null, CancellationToken.None);
        Assert.Equal(2, r1.Probed.Count);

        var r2 = new FakeRunner();
        var res2 = await new Scanner(r2, ProbeCache.Load(cachePath)).ScanAsync(root, null, CancellationToken.None);
        Assert.Empty(r2.Probed);
        Assert.Equal(2, res2.CacheHits);
        Assert.Equal(2, res2.Entries.Count);

        File.WriteAllText(Path.Combine(t.Path, "L", "a.mkv"), "aaaa-changed");
        var r3 = new FakeRunner();
        await new Scanner(r3, ProbeCache.Load(cachePath)).ScanAsync(root, null, CancellationToken.None);
        Assert.Single(r3.Probed);
        Assert.EndsWith("a.mkv", r3.Probed.Single());
    }

    [Fact]
    public async Task DeletedFiles_AreDroppedFromTheCache()
    {
        using var t = new TempDir();
        t.File("L/a.mkv"); var b = t.File("L/b.mkv");
        var root = new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) };
        var cachePath = Path.Combine(t.Path, "probe_cache.json");
        await new Scanner(new FakeRunner(), ProbeCache.Load(cachePath)).ScanAsync(root, null, CancellationToken.None);
        Assert.Equal(2, ProbeCache.Load(cachePath).Count);
        File.Delete(b);
        await new Scanner(new FakeRunner(), ProbeCache.Load(cachePath)).ScanAsync(root, null, CancellationToken.None);
        Assert.Equal(1, ProbeCache.Load(cachePath).Count);
    }

    [Fact]
    public async Task FailedProbes_BecomeErrorEntries_AndAreNotCached()
    {
        using var t = new TempDir();
        t.File("L/ok.mkv"); t.File("L/Movie_C.mp4"); t.File("L/boom.mkv");
        var runner = new FakeRunner
        {
            Respond = p =>
            {
                if (p.EndsWith("Movie_C.mp4")) return new ProbeRunResult("", "moov atom not found", 1, false);
                if (p.EndsWith("boom.mkv")) throw new InvalidOperationException("cannot start ffprobe");
                return new ProbeRunResult(Ffp.Json(Ffp.Mkv, 100, 1_000_000, new[] { Ffp.Video("h264", 1280, 720, 900_000), Ffp.Audio("aac", 2) }), "", 0, false);
            },
        };
        var cache = new ProbeCache(null);
        var res = await new Scanner(runner, cache).ScanAsync(new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) }, null, CancellationToken.None);
        Assert.Equal(3, res.Entries.Count);
        Assert.Equal(2, res.Failed);
        Assert.Equal(1, cache.Count);
        var snow = res.Entries.Single(e => e.FileName == "Movie_C.mp4");
        Assert.Contains("moov atom", snow.Probe.ProbeError);
        Assert.Equal(PlanAction.Error, Planner.Plan(snow, new PlannerOptions()).Action);
        Assert.Contains("cannot start", res.Entries.Single(e => e.FileName == "boom.mkv").Probe.ProbeError);
    }

    [Fact]
    public async Task Probes_RunThreeAtATime()
    {
        using var t = new TempDir();
        for (var i = 0; i < 12; i++) t.File($"L/f{i}.mkv");
        var runner = new FakeRunner { Delay = TimeSpan.FromMilliseconds(40) };
        await new Scanner(runner, new ProbeCache(null), 3).ScanAsync(new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) }, null, CancellationToken.None);
        Assert.Equal(3, runner.MaxConcurrent);
        Assert.Equal(12, runner.Probed.Count);
    }

    [Fact]
    public async Task Cancel_StopsEarly_ReturnsPartialResults_AndKeepsTheCache()
    {
        using var t = new TempDir();
        for (var i = 0; i < 40; i++) t.File($"L/f{i:00}.mkv");
        var cachePath = Path.Combine(t.Path, "probe_cache.json");
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner { Delay = TimeSpan.FromMilliseconds(30) };
        var n = 0;
        runner.OnProbe = _ => { if (Interlocked.Increment(ref n) == 6) cts.Cancel(); };
        var res = await new Scanner(runner, new ProbeCache(cachePath), 3).ScanAsync(new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) }, null, cts.Token);
        Assert.True(res.Cancelled);
        Assert.True(res.Entries.Count < 40);
        Assert.True(File.Exists(cachePath));
        // a rerun resumes: finished files come from the cache
        var runner2 = new FakeRunner();
        var res2 = await new Scanner(runner2, ProbeCache.Load(cachePath), 3).ScanAsync(new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) }, null, CancellationToken.None);
        Assert.False(res2.Cancelled);
        Assert.Equal(40, res2.Entries.Count);
        Assert.True(res2.CacheHits > 0);
        Assert.Equal(40, res2.CacheHits + runner2.Probed.Count);
    }

    [Fact]
    public async Task MissingFolder_IsReported_NotThrown()
    {
        using var t = new TempDir();
        t.File("L/a.mkv");
        var res = await new Scanner(new FakeRunner(), new ProbeCache(null)).ScanAsync(
            new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")), new LibraryRoot("X", Path.Combine(t.Path, "nope")) }, null, CancellationToken.None);
        Assert.Single(res.Entries);
        Assert.Single(res.MissingRoots);
    }

    [Fact]
    public async Task Results_AreSortedByLibraryThenPath_AndProgressReachesTotal()
    {
        using var t = new TempDir();
        t.File("S/b.mkv"); t.File("S/a.mkv"); t.File("M/z.mkv");
        var reports = new List<ScanProgress>();
        var prog = new SyncProgress<ScanProgress>(reports.Add);
        var res = await new Scanner(new FakeRunner(), new ProbeCache(null)).ScanAsync(
            new[] { new LibraryRoot("Series", Path.Combine(t.Path, "S")), new LibraryRoot("Movies", Path.Combine(t.Path, "M")) }, prog, CancellationToken.None);
        Assert.Equal(new[] { "Movies/z.mkv", "Series/a.mkv", "Series/b.mkv" }, res.Entries.Select(e => e.Library + "/" + e.RelativePath).ToArray());
        Assert.Equal(ScanPhase.Done, reports[^1].Phase);
        Assert.Equal(3, reports[^1].Done);
        Assert.Equal(3, reports[^1].Total);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) { _a = a; }
        public void Report(T value) { lock (this) _a(value); }
    }

    [SkippableFact]
    public async Task FfprobeRunner_PassesPathsWithSpecialCharactersIntact()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses a shell script as a fake ffprobe");
        using var t = new TempDir();
        var script = t.File("ffprobe", "#!/bin/sh\nfor last; do :; done\nprintf '%s' \"$last\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(t.Path, "Movie [2020] 'x' \"y\" & $HOME `z` ;ls #1 日本語.mkv");
        var r = await new FfprobeRunner(script).RunAsync(path, CancellationToken.None);
        Assert.Equal(path, r.Stdout);
    }
}

public class PlexVersionsTests
{
    [Fact]
    public async Task PlexVersionsFolder_IsSkippedByDefault_AndIncludedWhenAsked()
    {
        using var t = new TempDir();
        t.File("L/Movie/Movie (2016).mp4");
        t.File("L/Movie/Plex Versions/Original Quality/Movie (2016).mp4");
        var roots = new[] { new LibraryRoot("L", Path.Combine(t.Path, "L")) };
        var off = await new Scanner(new FakeRunner(), new ProbeCache(null)).ScanAsync(roots, null, CancellationToken.None);
        Assert.Single(off.Entries);
        var on = await new Scanner(new FakeRunner(), new ProbeCache(null), 3, includePlexVersions: true).ScanAsync(roots, null, CancellationToken.None);
        Assert.Equal(2, on.Entries.Count);
    }

    [Fact]
    public void Setting_DefaultsToOff() => Assert.False(new Settings().IncludePlexVersions);
}

public class SingleMediaFolderTests
{
    [Fact]
    public async Task OneFolder_IsScannedRecursively_AndLabelledByItsName()
    {
        using var t = new TempDir();
        t.File("Media/Movies/a.mkv");
        t.File("Media/Series/Show/Season 1/b.mkv");
        t.File("Media/Series/Show/Season 1/c.mkv");
        var res = await new Scanner(new FakeRunner(), new ProbeCache(null)).ScanAsync(
            new[] { new LibraryRoot("Media", Path.Combine(t.Path, "Media")) }, null, CancellationToken.None);
        Assert.Equal(3, res.Entries.Count);
        Assert.All(res.Entries, e => Assert.Equal("Media", e.Library));
        Assert.Contains(res.Entries, e => e.RelativePath.Replace('\\', '/') == "Series/Show/Season 1/c.mkv");
    }

    [Fact]
    public async Task ScanningOneGroupAtATime_KeepsTheOtherGroupsCacheEntries()
    {
        using var t = new TempDir();
        t.File("Media/Movies/a.mkv");
        t.File("Media/Series/s/b.mkv");
        t.File("Media/Series/s/c.mkv");
        var cachePath = Path.Combine(t.Path, "cache.json");
        var movies = new[] { new LibraryRoot("Movies", Path.Combine(t.Path, "Media", "Movies")) };
        var series = new[] { new LibraryRoot("Series", Path.Combine(t.Path, "Media", "Series")) };

        var r1 = await new Scanner(new FakeRunner(), new ProbeCache(cachePath)).ScanAsync(series, null, CancellationToken.None);
        Assert.Equal(2, r1.Entries.Count);
        var r2 = await new Scanner(new FakeRunner(), ProbeCache.Load(cachePath)).ScanAsync(movies, null, CancellationToken.None);
        Assert.Single(r2.Entries);
        Assert.All(r2.Entries, e => Assert.Equal("Movies", e.Library));

        var runner = new FakeRunner();
        var r3 = await new Scanner(runner, ProbeCache.Load(cachePath)).ScanAsync(series, null, CancellationToken.None);
        Assert.Equal(2, r3.CacheHits);
        Assert.Empty(runner.Probed);
    }
}
