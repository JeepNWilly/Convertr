// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.IO.Enumeration;

namespace Convertr.Core;

public sealed record LibraryRoot(string Label, string Path);

public enum ScanPhase { Enumerating, Probing, Done }

public sealed record ScanProgress(ScanPhase Phase, int Done, int Total, int FromCache, int Failed, string? Current);

public sealed class ScanResult
{
    public List<ScanEntry> Entries { get; } = new();
    public bool Cancelled { get; set; }
    public int Enumerated { get; set; }
    public int CacheHits { get; set; }
    public int Probed { get; set; }
    public int Failed { get; set; }
    public List<string> MissingRoots { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Walks the library folders and probes every video file with ffprobe (default 3 at a time), using the cache for
/// files that have not changed. Strictly read-only toward the library: it opens files only through ffprobe and never
/// writes, renames, or deletes anything there. The only file it writes is the probe cache next to the exe.
/// </summary>
public sealed class Scanner
{
    public static readonly string[] VideoExtensions =
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".flv", ".divx" };

    public static readonly string[] SidecarExtensions = { ".srt", ".sub", ".ass", ".ssa", ".idx", ".vtt" };

    /// <summary>Folders never scanned: NAS/system folders and Convertr's own holding folders.</summary>
    public static readonly string[] SkipFolders =
        { "ROMS", "@eaDir", "#recycle", "$RECYCLE.BIN", "System Volume Information", "_Convertr_Originals", "_Convertr_Undone" };

    /// <summary>Plex's own "optimized versions" folder. Skipped by default (Plex manages those copies); can be included in Setup.</summary>
    public const string PlexVersionsFolder = "Plex Versions";

    public static HashSet<string> SkipSetFor(bool includePlexVersions)
    {
        var set = new HashSet<string>(SkipFolders, StringComparer.OrdinalIgnoreCase);
        if (!includePlexVersions) set.Add(PlexVersionsFolder);
        return set;
    }

    private static readonly HashSet<string> VideoSet = new(VideoExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SidecarSet = new(SidecarExtensions, StringComparer.OrdinalIgnoreCase);

    private readonly IProbeRunner _runner;
    private readonly ProbeCache _cache;
    private readonly int _threads;
    private readonly HashSet<string> _skip;

    public Scanner(IProbeRunner runner, ProbeCache cache, int parallelProbes = 3, bool includePlexVersions = false)
    {
        _runner = runner;
        _cache = cache;
        _threads = Math.Max(1, parallelProbes);
        _skip = SkipSetFor(includePlexVersions);
    }

    private sealed record FileItem(string FullPath, long Length, long MtimeTicks);

    private sealed record WorkItem(string Library, string Root, string FullPath, string Relative, long Length, long MtimeTicks, bool ExternalSubs);

    /// <summary>Lists every file under a folder (read-only directory enumeration), not entering the folders named in <paramref name="skip"/>.</summary>
    public static IEnumerable<(string FullPath, long Length, long MtimeTicks)> EnumerateFiles(string root, IReadOnlySet<string>? skip = null)
    {
        skip ??= SkipSetFor(false);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var en = new FileSystemEnumerable<FileItem>(root, (ref FileSystemEntry e) =>
                new FileItem(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcTicks), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => e.IsDirectory && !skip.Contains(e.FileName.ToString()) && !IsLinkedDirectory(ref e),
        };
        foreach (var f in en) yield return (f.FullPath, f.Length, f.MtimeTicks);
    }

    /// <summary>
    /// Linux/macOS: a symbolic link to a folder is not entered (it could lead outside the media folder or loop). Windows keeps its behaviour unchanged.
    /// </summary>
    private static bool IsLinkedDirectory(ref FileSystemEntry e) =>
        !OperatingSystem.IsWindows() && (e.Attributes & FileAttributes.ReparsePoint) != 0;

    private List<WorkItem> Enumerate(IEnumerable<LibraryRoot> roots, ScanResult result, CancellationToken ct)
    {
        var work = new List<WorkItem>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.Path)) { result.MissingRoots.Add(root.Path); continue; }
            var rootPath = root.Path.TrimEnd('\\', '/');
            var videos = new List<FileItem>();
            var sidecars = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var (full, len, mt) in EnumerateFiles(rootPath, _skip))
            {
                if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(full);
                if (VideoSet.Contains(ext)) videos.Add(new FileItem(full, len, mt));
                else if (SidecarSet.Contains(ext))
                {
                    var dir = Path.GetDirectoryName(full) ?? "";
                    if (!sidecars.TryGetValue(dir, out var list)) sidecars[dir] = list = new List<string>();
                    list.Add(Path.GetFileName(full));
                }
            }
            foreach (var v in videos)
            {
                var dir = Path.GetDirectoryName(v.FullPath) ?? "";
                var prefix = Path.GetFileNameWithoutExtension(v.FullPath) + ".";
                var hasSub = sidecars.TryGetValue(dir, out var names) &&
                             names.Any(nm => nm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                var rel = v.FullPath.Length > rootPath.Length ? v.FullPath[rootPath.Length..].TrimStart('\\', '/') : v.FullPath;
                work.Add(new WorkItem(root.Label, rootPath, v.FullPath, rel, v.Length, v.MtimeTicks, hasSub));
            }
        }
        return work;
    }

    public async Task<ScanResult> ScanAsync(IEnumerable<LibraryRoot> roots, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult();
        var rootList = roots.ToList();
        List<WorkItem> work;
        progress?.Report(new ScanProgress(ScanPhase.Enumerating, 0, 0, 0, 0, null));
        try
        {
            work = await Task.Run(() => Enumerate(rootList, result, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Elapsed = sw.Elapsed;
            return result;
        }
        result.Enumerated = work.Count;

        var slots = new ScanEntry?[work.Count];
        var done = 0; var hits = 0; var probed = 0; var failed = 0;
        var gate = new object();
        var lastReport = Stopwatch.StartNew();

        void Report(string? current, bool force)
        {
            if (progress == null) return;
            lock (gate)
            {
                if (!force && lastReport.ElapsedMilliseconds < 100) return;
                lastReport.Restart();
            }
            progress.Report(new ScanProgress(ScanPhase.Probing, Volatile.Read(ref done), work.Count, Volatile.Read(ref hits), Volatile.Read(ref failed), current));
        }

        Report(null, true);
        try
        {
            var indexed = work.Select((w, i) => (w, i));
            await Parallel.ForEachAsync(indexed, new ParallelOptions { MaxDegreeOfParallelism = _threads, CancellationToken = ct },
                async (pair, token) =>
                {
                    var (w, i) = pair;
                    ProbeInfo info;
                    if (_cache.TryGet(w.FullPath, w.Length, w.MtimeTicks, out var cached))
                    {
                        info = cached;
                        Interlocked.Increment(ref hits);
                    }
                    else
                    {
                        info = await ProbeOneAsync(w.FullPath, token).ConfigureAwait(false);
                        Interlocked.Increment(ref probed);
                        if (info.ProbeError != null) Interlocked.Increment(ref failed);
                        else
                        {
                            _cache.Put(w.FullPath, w.Length, w.MtimeTicks, info);
                            if (_cache.PendingWrites >= 250) { try { _cache.Save(); } catch { /* retried at the end */ } }
                        }
                    }
                    slots[i] = new ScanEntry
                    {
                        Library = w.Library,
                        RelativePath = w.Relative,
                        FullPath = w.FullPath,
                        SizeBytes = w.Length,
                        MtimeUtcTicks = w.MtimeTicks,
                        HasExternalSubs = w.ExternalSubs,
                        Probe = info,
                    };
                    Interlocked.Increment(ref done);
                    Report(w.Relative, false);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        finally
        {
            try { _cache.Save(); } catch { /* cache is an optimisation only */ }
        }

        result.Entries.AddRange(slots.Where(s => s != null).Select(s => s!)
            .OrderBy(s => s.Library, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase));
        result.CacheHits = hits;
        result.Probed = probed;
        result.Failed = failed;

        if (!result.Cancelled)
        {
            var seen = new HashSet<string>(work.Select(w => w.FullPath), PathCompare.Comparer);
            if (_cache.Prune(rootList.Where(r => !result.MissingRoots.Contains(r.Path)).Select(r => r.Path), seen) > 0)
            {
                try { _cache.Save(); } catch { /* ignore */ }
            }
        }
        result.Elapsed = sw.Elapsed;
        progress?.Report(new ScanProgress(ScanPhase.Done, done, work.Count, hits, failed, null));
        return result;
    }

    private async Task<ProbeInfo> ProbeOneAsync(string path, CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync(path, ct).ConfigureAwait(false);
            if (r.TimedOut) return ProbeParser.Failed("ffprobe timed out");
            return ProbeParser.Parse(r.Stdout, r.Stderr, r.ExitCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ProbeParser.Failed("ffprobe could not run: " + ex.Message);
        }
    }
}
