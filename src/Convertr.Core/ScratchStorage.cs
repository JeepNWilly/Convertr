// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Security.Cryptography;

namespace Convertr.Core;

/// <summary>
/// Convertr_svc scratch workflow (DESIGN.md section 7A): the source is copied to a local scratch disk, ffmpeg works only there,
/// the engine decides keep-or-discard BEFORE anything goes back, and only a verified smaller result is copied to the library as a
/// temporary file that <see cref="Swapper"/> then renames into place (original goes to the holding area). The library is never
/// written by ffmpeg and a half-copied file never has its final name.
/// </summary>
public sealed class ScratchStorage : IJobStorage
{
    public const string MarkerName = ".convertr-scratch";
    private const int BufferSize = 4 * 1024 * 1024;
    private const long Gb = 1024L * 1024 * 1024;

    private readonly Settings _s;
    private readonly Swapper _swapper;
    private readonly RunLog _log;
    private readonly Func<string, long> _free;
    private readonly SemaphoreSlim _transfers;
    private readonly object _gate = new();
    private readonly Dictionary<int, (string Dir, long Reserved)> _jobs = new();
    private long _reserved;
    private long _autoBudget = -1;

    public ScratchStorage(Settings s, Swapper swapper, RunLog log, Func<string, long>? freeBytes = null)
    {
        _s = s; _swapper = swapper; _log = log;
        _free = freeBytes ?? DefaultFree;
        _transfers = new SemaphoreSlim(Math.Clamp(s.MaxConcurrentTransfers, 1, 8));
    }

    public string Name => "scratch";
    public bool UsesScratch => true;
    public string Root => _s.ScratchFolder;

    /// <summary>Bytes currently reserved by running jobs.</summary>
    public long ReservedBytes { get { lock (_gate) return _reserved; } }

    private static long DefaultFree(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; } catch { return long.MaxValue; }
    }

    /// <summary>What one job needs on the scratch disk: the source copy plus an output as big as the source (it is discarded when not smaller), plus a margin.</summary>
    public static long NeededBytes(long sourceBytes) => sourceBytes * 2 + Gb / 2;

    /// <summary>The scratch folder must exist, be writable and either be empty or carry the marker file: Convertr never deletes inside an unmarked folder.</summary>
    public void EnsureRoot()
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) throw new ScratchException(ScratchProblem.Config, "the scratch folder is not set");
        try
        {
            Directory.CreateDirectory(root);
            var marker = Path.Combine(root, MarkerName);
            if (!File.Exists(marker))
            {
                if (Directory.EnumerateFileSystemEntries(root).Any())
                    throw new ScratchException(ScratchProblem.Config, $"the scratch folder '{root}' is not empty and not marked for Convertr; use an empty folder (it is marked on first use)");
                File.WriteAllText(marker, "Convertr scratch folder. Everything in job-* folders here is temporary.\n");
            }
        }
        catch (ScratchException) { throw; }
        catch (Exception ex) { throw new ScratchException(ScratchProblem.Config, $"the scratch folder '{root}' cannot be used: {ex.Message}", ex); }
    }

    private long Budget()
    {
        if (_s.ScratchBudgetGb > 0) return _s.ScratchBudgetGb * Gb;
        lock (_gate)
        {
            if (_autoBudget < 0) _autoBudget = Math.Max(0, (long)(_free(Root) * 0.9) - 2 * Gb);
            return _autoBudget;
        }
    }

    public async Task<StagedJob> StageAsync(QueueJob job, string mediaFolder, Action<double> progress, CancellationToken ct)
    {
        EnsureRoot();
        var e = job.Entry;
        var need = NeededBytes(e.SizeBytes);

        // originals must be movable by a rename on the library's own mount (a cross-mount "move" is a slow copy and not atomic)
        if (_s.Originals == OriginalsHandling.MoveToHolding)
        {
            var holding = HoldingArea.HoldingPathFor(mediaFolder, e.FullPath, _s);
            if (!MountGuard.SameMount(e.FullPath, holding))
                throw new ScratchException(ScratchProblem.Config,
                    $"the originals folder ({Path.GetDirectoryName(holding)}) is on a different mount than the library; choose a Media folder inside a share whose parent folder is writable, or set originals to rename-in-place");
        }

        if (_s.MaxScratchPerJobGb > 0 && need > _s.MaxScratchPerJobGb * Gb)
            throw new ScratchException(ScratchProblem.TooBig, $"needs about {need / (double)Gb:0.0} GB of scratch, more than the per-file limit of {_s.MaxScratchPerJobGb} GB");
        var budget = Budget();
        if (need > budget)
            throw new ScratchException(ScratchProblem.NoSpace, $"needs about {need / (double)Gb:0.0} GB of scratch but the budget is {budget / (double)Gb:0.0} GB");

        // admission: wait while other jobs hold the budget
        var announced = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_reserved + need <= budget) { _reserved += need; break; }
            }
            if (!announced) { announced = true; _log.Write($"scratch: waiting for room ({need / (double)Gb:0.0} GB needed) for {e.FileName}"); }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        string? dir = null;
        try
        {
            var free = _free(Root);
            if (free < need)
                throw new ScratchException(ScratchProblem.NoSpace, $"not enough free space on the scratch disk (need about {need / (double)Gb:0.0} GB, free {free / (double)Gb:0.0} GB)");

            dir = Path.Combine(Root, "job-" + job.Id + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            lock (_gate) _jobs[job.Id] = (dir, need);
            var local = Path.Combine(dir, "source." + (string.IsNullOrEmpty(e.Extension) ? "bin" : e.Extension));

            _log.Write($"scratch: copying in {e.FullPath} ({e.SizeBytes / 1048576.0:0} MB) -> {local}");
            await _transfers.WaitAsync(ct).ConfigureAwait(false);
            try { await CopyAsync(e.FullPath, local, progress, ct, hash: false).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Library(ex, e.FullPath, "copying in");
            }
            finally { _transfers.Release(); }

            var li = new FileInfo(local);
            if (li.Length != e.SizeBytes) throw new ScratchException(ScratchProblem.Copy, $"the copy on the scratch disk has a different size ({li.Length} instead of {e.SizeBytes} bytes)");
            // the library file must not have changed while it was being copied
            var after = new FileInfo(e.FullPath);
            if (!after.Exists || after.Length != e.SizeBytes || Math.Abs(after.LastWriteTimeUtc.Ticks - e.MtimeUtcTicks) > TimeSpan.TicksPerSecond * 2)
                throw new ScratchException(ScratchProblem.Copy, "the file changed while it was being copied in (scan again)");
            return new StagedJob(local, Path.Combine(dir, "out.convertr-part"), dir);
        }
        catch
        {
            Release(job, keep: false);
            throw;
        }
    }

    public async Task CopyBackAsync(QueueJob job, string localPart, Action<double> progress, CancellationToken ct)
    {
        var dest = job.PartPath;
        var size = new FileInfo(localPart).Length;
        if (_s.Originals == OriginalsHandling.MoveToHolding && !MountGuard.SameMount(job.Entry.FullPath, job.HoldingPath))
            throw new ScratchException(ScratchProblem.Config, "the originals folder is on a different mount than the library");
        var free = _free(Path.GetDirectoryName(dest) ?? "");
        if (free < size + 256L * 1024 * 1024)
            throw new ScratchException(ScratchProblem.NoSpace, $"not enough free space in the library for the result (need {size / 1048576.0:0} MB, free {free / 1048576.0:0} MB)");

        _log.Write($"scratch: copying back {localPart} ({size / 1048576.0:0} MB) -> {dest}");
        await _transfers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? srcHash;
            try { srcHash = await CopyAsync(localPart, dest, progress, ct, hash: _s.CopyBackVerify == CopyBackVerify.Checksum).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(dest);
                throw Library(ex, dest, "copying back");
            }
            catch { TryDelete(dest); throw; }

            var di = new FileInfo(dest);
            if (!di.Exists || di.Length != size)
            {
                TryDelete(dest);
                throw new ScratchException(ScratchProblem.Copy, $"the copy in the library has a different size ({(di.Exists ? di.Length : 0)} instead of {size} bytes); temporary file deleted, original untouched");
            }
            if (srcHash != null)
            {
                var dstHash = await HashAsync(dest, ct).ConfigureAwait(false);
                if (!string.Equals(srcHash, dstHash, StringComparison.Ordinal))
                {
                    TryDelete(dest);
                    throw new ScratchException(ScratchProblem.Copy, "the checksum of the copy in the library does not match; temporary file deleted, original untouched");
                }
            }
        }
        finally { _transfers.Release(); }
    }

    public void Commit(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder) =>
        _swapper.Swap(runId, job, srcMtimeUtc, srcCtimeUtc, mediaFolder);

    public bool Recover(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder) =>
        _swapper.Recover(runId, job, srcMtimeUtc, srcCtimeUtc, mediaFolder);

    public void Release(QueueJob job, bool keep)
    {
        (string Dir, long Reserved) info;
        lock (_gate)
        {
            if (!_jobs.Remove(job.Id, out info)) return;
            _reserved = Math.Max(0, _reserved - info.Reserved);
        }
        try
        {
            if (!Directory.Exists(info.Dir)) return;
            if (keep && _s.KeepScratchOnFailure)
            {
                var kept = Path.Combine(Root, "kept-" + Path.GetFileName(info.Dir));
                Directory.Move(info.Dir, kept);
                _log.Write("scratch: kept for inspection: " + kept);
            }
            else Directory.Delete(info.Dir, true);
        }
        catch (Exception ex) { _log.Write("scratch: could not clean " + info.Dir + ": " + ex.Message); }
    }

    public void CleanStale()
    {
        try
        {
            var root = Root;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || !File.Exists(Path.Combine(root, MarkerName))) return;
            HashSet<string> active;
            lock (_gate) active = _jobs.Values.Select(v => v.Dir).ToHashSet(StringComparer.Ordinal);
            foreach (var d in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(d);
                try
                {
                    if (name.StartsWith("job-", StringComparison.Ordinal) && !active.Contains(d))
                    { Directory.Delete(d, true); _log.Write("scratch: removed leftover " + d); }
                    else if (name.StartsWith("kept-", StringComparison.Ordinal) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(_s.StaleScratchHours))
                    { Directory.Delete(d, true); _log.Write("scratch: removed old kept folder " + d); }
                }
                catch (Exception ex) { _log.Write("scratch: could not remove " + d + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { _log.Write("scratch: cleanup failed: " + ex.Message); }
    }

    /// <summary>Bytes in scratch job folders (for the status page).</summary>
    public long UsedBytes()
    {
        try
        {
            if (!Directory.Exists(Root)) return 0;
            return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    // ---------------- helpers ----------------
    /// <summary>A library-side I/O failure: when the library folder is gone the NAS is offline (retry later), otherwise the copy failed.</summary>
    private static ScratchException Library(Exception ex, string libraryPath, string what)
    {
        var dir = Path.GetDirectoryName(libraryPath);
        var offline = string.IsNullOrEmpty(dir) || !Directory.Exists(dir)
                      || ex.Message.Contains("Stale file handle", StringComparison.OrdinalIgnoreCase)
                      || ex.Message.Contains("Host is down", StringComparison.OrdinalIgnoreCase)
                      || ex.Message.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
                      || ex.Message.Contains("Transport endpoint", StringComparison.OrdinalIgnoreCase);
        return offline
            ? new ScratchException(ScratchProblem.Offline, $"the library is not reachable while {what} ({ex.Message}); nothing was changed, try again when it is back", ex)
            : new ScratchException(ScratchProblem.Copy, $"{what} failed: {ex.Message}", ex);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    private async Task<string?> CopyAsync(string src, string dst, Action<double> progress, CancellationToken ct, bool hash)
    {
        var total = new FileInfo(src).Length;
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buf = new byte[BufferSize];
        long done = 0;
        await using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            int n;
            while ((n = await input.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                sha?.AppendData(buf, 0, n);
                done += n;
                if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(500)) { lastReport = sw.Elapsed; progress(total > 0 ? (double)done / total : 1); }
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(true);
        }
        progress(1);
        return sha == null ? null : Convert.ToHexString(sha.GetHashAndReset());
    }
}

/// <summary>Finds out whether two paths live on the same mount (Linux: /proc/self/mountinfo, longest mount point wins). Elsewhere: assumed the same.</summary>
public static class MountGuard
{
    public static bool SameMount(string pathA, string pathB)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mountinfo")) return true;
            var mounts = ReadMounts();
            var a = MountOf(NearestExisting(pathA), mounts);
            var b = MountOf(NearestExisting(pathB), mounts);
            return a == null || b == null || a == b;
        }
        catch { return true; }
    }

    private static string NearestExisting(string path)
    {
        var p = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(p) && !Directory.Exists(p) && !File.Exists(p)) p = Path.GetDirectoryName(p) ?? "";
        return string.IsNullOrEmpty(p) ? "/" : ResolveLinks(p);
    }

    private static string ResolveLinks(string p)
    {
        try
        {
            FileSystemInfo fi = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            return fi.ResolveLinkTarget(true)?.FullName ?? p;
        }
        catch { return p; }
    }

    private static List<string> ReadMounts()
    {
        var list = new List<string>();
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var f = line.Split(' ');
            if (f.Length > 4) list.Add(Unescape(f[4]));
        }
        return list;
    }

    private static string? MountOf(string path, List<string> mounts)
    {
        string? best = null;
        foreach (var m in mounts)
        {
            var hit = m == "/" || path == m || path.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (hit && (best == null || m.Length > best.Length)) best = m;
        }
        return best;
    }

    private static string Unescape(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
}
