// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Globalization;

namespace Convertr.Core;

/// <summary>Builds the queue for a run and previews single commands (used by the Details dialog and Dry run).</summary>
public static class QueueBuilder
{
    public const string PartSuffix = ".convertr-part";

    public static bool NeedsChapterFile(PlanItem plan, Settings s) => s.CleanJunkMetadata && !s.DropChapters && plan.Metadata.JunkChapters > 0;

    /// <summary>The exact command that would run for this file, with the real final and temporary names.</summary>
    public static FfmpegCommand Preview(ScanEntry e, PlanItem plan, Settings s, out string finalPath, out string partPath)
    {
        var probe = FfmpegCommandBuilder.Build(e, plan, s, "x", null);
        finalPath = Path.Combine(Path.GetDirectoryName(e.FullPath) ?? "", e.BaseName + "." + probe.Extension);
        partPath = finalPath + PartSuffix;
        return FfmpegCommandBuilder.Build(e, plan, s, partPath, NeedsChapterFile(plan, s) ? Path.Combine(Path.GetDirectoryName(partPath) ?? "", "chapters.ffmetadata (temporary)") : null);
    }

    /// <summary>One job per row that writes a file. A final name that is already taken (or used twice) is marked Skipped, never overwritten.</summary>
    public static QueueFile Build(IEnumerable<(ScanEntry Entry, PlanItem Plan)> rows, Settings s, string mediaFolder, string runId)
    {
        var q = new QueueFile { RunId = runId, MediaFolder = mediaFolder, DryRun = s.DryRun };
        var used = new HashSet<string>(PathCompare.Comparer);
        var id = 1;
        foreach (var (e, p) in rows)
        {
            if (!p.WritesFile || e.Probe.Video == null) continue;
            Preview(e, p, s, out var final, out var part);
            var job = new QueueJob { Id = id++, Entry = e, Plan = p, FinalPath = final, PartPath = part };
            var samePlace = PathCompare.Equal(final, e.FullPath);
            if (!samePlace && File.Exists(final)) { job.State = JobState.Skipped; job.Message = "not started: a file named " + Path.GetFileName(final) + " already exists in that folder"; }
            else if (!used.Add(final)) { job.State = JobState.Skipped; job.Message = "not started: another file in this run would be written to the same name"; }
            else if (File.Exists(part)) { job.State = JobState.Skipped; job.Message = "not started: a leftover temporary file exists: " + Path.GetFileName(part); }
            q.Jobs.Add(job);
        }
        return q;
    }
}

/// <summary>The "only run between HH:mm and HH:mm" option. A window that crosses midnight (22:00 to 06:00) works too.</summary>
public static class RunWindow
{
    public static bool IsAllowed(DateTime now, bool enabled, string start, string end)
    {
        if (!enabled) return true;
        if (!TimeSpan.TryParse(start, out var a) || !TimeSpan.TryParse(end, out var b)) return true;
        var t = now.TimeOfDay;
        if (a == b) return true;
        return a < b ? t >= a && t < b : t >= a || t < b;
    }
}

public sealed record EngineProgress(
    QueueJob? Job, double JobFraction, double? Speed, TimeSpan? JobEta, int FinishedJobs, int TotalJobs,
    long ProcessedBytes, long TotalBytes, TimeSpan Elapsed, TimeSpan? OverallEta);

public sealed record RunSummary(
    int Done, int NotWorthIt, int Failed, int Skipped, int Pending, long OriginalBytes, long NewBytes, TimeSpan Elapsed, bool Stopped, string StopReason)
{
    public long SavedBytes => OriginalBytes - NewBytes;
}

/// <summary>
/// The conversion engine (DESIGN.md section 5): runs the queue one job at a time (or more, if the Setup option says so):
/// build command, encode to a .convertr-part file, verify, swap, journal. A job that fails leaves the original untouched and no partial file.
/// </summary>
public sealed class ConversionEngine
{
    private readonly Settings _s;
    private readonly IFfmpegRunner _ff;
    private readonly IProbeRunner _probe;
    private readonly RunLog _log;
    private readonly UndoJournal _journal;
    private readonly IJobStorage _storage;
    private readonly Func<string, long> _freeBytes;
    private readonly string? _queuePath;
    private readonly object _gate = new();
    private readonly List<ProcessControl> _active = new();
    private CancellationTokenSource? _killCts;
    private volatile bool _stopAfterCurrent;
    private volatile bool _paused;
    private TaskCompletionSource _resumeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _stopReason = "";

    public event Action<QueueJob>? JobChanged;
    /// <summary>Text for the status line when nothing is encoding (for example waiting for the run window).</summary>
    public event Action<string>? StatusChanged;
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;
    public TimeSpan WindowPoll { get; set; } = TimeSpan.FromSeconds(30);
    public event Action<EngineProgress>? ProgressChanged;

    public bool Paused => _paused;

    public ConversionEngine(Settings s, IFfmpegRunner ff, IProbeRunner probe, RunLog log, UndoJournal journal, string? queuePath,
        Func<string, long>? freeBytes = null, TimeSpan? swapRetryDelay = null, IJobStorage? storage = null)
    {
        _s = s; _ff = ff; _probe = probe; _log = log; _journal = journal; _queuePath = queuePath;
        _storage = storage ?? new InPlaceStorage(new Swapper(s, journal, log, 5, swapRetryDelay));
        _freeBytes = freeBytes ?? DefaultFree;
        _resumeTcs.SetResult();
    }

    private static long DefaultFree(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; } catch { return long.MaxValue; }
    }

    // ---------------- controls ----------------
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var c in _active) { try { c.Suspend(); } catch { /* ignore */ } }
        }
        _log.Write("paused");
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            foreach (var c in _active) { try { c.Resume(); } catch { /* ignore */ } }
            _resumeTcs.TrySetResult();
        }
        _log.Write("resumed");
    }

    /// <summary>Finish the file that is running, then stop (nothing new is started).</summary>
    public void StopAfterCurrent() { _stopAfterCurrent = true; _log.Write("stop requested: finishing the current file"); Resume(); }

    /// <summary>Stop now: the running ffmpeg is killed and its partial file deleted. Unfinished jobs stay in the queue.</summary>
    public void Stop()
    {
        _stopAfterCurrent = true;
        _log.Write("stop requested: cancelling the current file");
        try { _killCts?.Cancel(); } catch { /* ignore */ }
        Resume();
    }

    // ---------------- run ----------------
    public async Task<RunSummary> RunAsync(QueueFile q, CancellationToken externalCt = default)
    {
        _stopAfterCurrent = false; _stopReason = "";
        _killCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var sw = Stopwatch.StartNew();
        var total = q.Jobs.Count(j => !j.IsFinal || j.State == JobState.Done);
        long totalBytes = q.Jobs.Where(j => j.State != JobState.Skipped).Sum(j => j.Entry.SizeBytes);
        _log.Write($"run {q.RunId} started: {q.Jobs.Count(j => !j.IsFinal)} files, media folder {q.MediaFolder}, mode {_s.Mode}, container {_s.Container}, dry run {(q.DryRun ? "yes" : "no")}");

        if (q.DryRun)
        {
            foreach (var j in q.Jobs.Where(j => !j.IsFinal))
            {
                var cmd = QueueBuilder.Preview(j.Entry, j.Plan, _s, out _, out _);
                _log.Write($"[dry run] {j.Entry.RelativePath}: {j.Plan.ActionLabel}: {cmd.ToCommandLine()}");
            }
            _log.Write("dry run finished: no file was touched");
            return Summarize(q, sw.Elapsed, false, "");
        }

        if (_storage.UsesScratch) _storage.CleanStale();
        Recover(q);
        Save(q);

        var workers = _s.Mode == EncoderMode.Gpu ? _s.ParallelGpuJobs : _s.ParallelCpuJobs;
        long batchBytes = 0;
        var next = 0;
        var pending = q.Jobs.Where(j => j.State == JobState.Pending).ToList();
        long processedBefore = q.Jobs.Where(j => j.State is JobState.Done or JobState.NotWorthIt or JobState.Failed).Sum(j => j.Entry.SizeBytes);
        var activeFraction = new Dictionary<int, double>();
        long finishedBytesThisRun = 0;
        int finishedJobs = q.Jobs.Count(j => j.IsFinal && j.State != JobState.Skipped);

        void Report(QueueJob? job, double frac, double? speed, TimeSpan? eta)
        {
            long active;
            lock (activeFraction) { active = (long)q.Jobs.Where(j => activeFraction.ContainsKey(j.Id)).Sum(j => j.Entry.SizeBytes * activeFraction[j.Id]); }
            var processed = processedBefore + finishedBytesThisRun + active;
            var elapsed = sw.Elapsed;
            TimeSpan? overall = null;
            var doneThisRun = finishedBytesThisRun + active;
            if (doneThisRun > 0 && elapsed.TotalSeconds > 3)
            {
                var rate = doneThisRun / elapsed.TotalSeconds;
                var remaining = Math.Max(0, totalBytes - processed);
                if (rate > 1) overall = TimeSpan.FromSeconds(remaining / rate);
            }
            ProgressChanged?.Invoke(new EngineProgress(job, frac, speed, eta, finishedJobs, total, processed, totalBytes, elapsed, overall));
        }

        async Task Worker()
        {
            while (true)
            {
                await _resumeTcs.Task.ConfigureAwait(false);
                if (_stopAfterCurrent || _killCts.IsCancellationRequested) return;
                var waited = false;
                while (!RunWindow.IsAllowed(Now(), _s.QuietHoursEnabled, _s.QuietHoursStart, _s.QuietHoursEnd) && !_stopAfterCurrent && !_killCts.IsCancellationRequested)
                {
                    if (!waited) { waited = true; _log.Write($"waiting: outside the run window {_s.QuietHoursStart} - {_s.QuietHoursEnd}"); }
                    StatusChanged?.Invoke($"Waiting for the run window ({_s.QuietHoursStart} - {_s.QuietHoursEnd}); the queue continues by itself.");
                    try { await Task.Delay(WindowPoll, _killCts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
                if (_stopAfterCurrent || _killCts.IsCancellationRequested) return;
                QueueJob job;
                lock (_gate)
                {
                    if (next >= pending.Count) return;
                    job = pending[next];
                    if (_s.BatchLimitGb > 0 && batchBytes >= _s.BatchLimitGb * 1024L * 1024L * 1024L)
                    {
                        _stopReason = $"batch limit of {_s.BatchLimitGb} GB reached";
                        _stopAfterCurrent = true;
                        return;
                    }
                    next++;
                    batchBytes += job.Entry.SizeBytes;
                }
                lock (activeFraction) { activeFraction[job.Id] = 0; }
                await RunJobAsync(q, job, (f, sp, eta) => { lock (activeFraction) { activeFraction[job.Id] = f; } Report(job, f, sp, eta); }, r => { _stopReason = r; _stopAfterCurrent = true; }).ConfigureAwait(false);
                lock (activeFraction) { activeFraction.Remove(job.Id); }
                if (job.IsFinal && job.State != JobState.Skipped) { finishedBytesThisRun += job.Entry.SizeBytes; finishedJobs++; }
                Report(null, 0, null, null);
            }
        }

        var tasks = Enumerable.Range(0, Math.Max(1, workers)).Select(_ => Task.Run(Worker)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var stopped = q.Jobs.Any(j => !j.IsFinal);
        if (stopped && _stopReason == "") _stopReason = "stopped by the user";
        Save(q);
        var sum = Summarize(q, sw.Elapsed, stopped, _stopReason);
        _log.Write($"run finished: {sum.Done} converted, {sum.NotWorthIt} not worth it, {sum.Failed} failed, {sum.Skipped} skipped, {sum.Pending} left in the queue; saved {sum.SavedBytes / 1048576.0:0} MB in {sum.Elapsed:hh\\:mm\\:ss}" + (stopped ? " (" + _stopReason + ")" : ""));
        return sum;
    }

    private static RunSummary Summarize(QueueFile q, TimeSpan elapsed, bool stopped, string reason)
    {
        var done = q.Jobs.Where(j => j.State == JobState.Done).ToList();
        return new RunSummary(done.Count, q.Jobs.Count(j => j.State == JobState.NotWorthIt), q.Jobs.Count(j => j.State == JobState.Failed),
            q.Jobs.Count(j => j.State == JobState.Skipped), q.Jobs.Count(j => !j.IsFinal),
            done.Sum(j => j.Entry.SizeBytes), done.Sum(j => j.OutputBytes), elapsed, stopped, reason);
    }

    private void Save(QueueFile q)
    {
        if (_queuePath == null) return;
        try { QueueStore.Save(q, _queuePath); } catch (Exception ex) { _log.Write("could not save the queue: " + ex.Message); }
    }

    private void Changed(QueueFile q, QueueJob j, JobState st, string? msg = null)
    {
        j.State = st;
        if (msg != null) j.Message = msg;
        Save(q);
        JobChanged?.Invoke(j);
    }

    /// <summary>After a crash or restart: unfinished jobs go back to Pending, half-done swaps are finished or rolled back, leftover temporary files are deleted.</summary>
    private void Recover(QueueFile q)
    {
        foreach (var j in q.Jobs.Where(j => !j.IsFinal))
        {
            if (j.State == JobState.Swapping)
            {
                try
                {
                    var ctime = File.Exists(j.HoldingPath) ? PlatformServices.Current.FileTimes.GetCreationUtc(j.HoldingPath) : DateTime.MinValue;
                    var ok = _storage.Recover(q.RunId, j, new DateTime(j.Entry.MtimeUtcTicks, DateTimeKind.Utc), ctime, q.MediaFolder);
                    j.State = ok ? JobState.Done : JobState.Pending;
                    if (ok) { j.OutputBytes = new FileInfo(j.FinalPath).Length; j.Message = "completed after restart"; }
                }
                catch (Exception ex) { j.State = JobState.Failed; j.Message = "recovery failed: " + ex.Message; _log.Write("recovery failed for " + j.Entry.FullPath + ": " + ex.Message); }
            }
            else if (j.State is JobState.Encoding or JobState.Verifying or JobState.CopyingIn or JobState.CopyingBack)
            {
                TryDelete(j.PartPath);
                j.State = JobState.Pending;
            }
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { _log.Write("could not delete " + path + ": " + ex.Message); }
    }

    private async Task RunJobAsync(QueueFile q, QueueJob job, Action<double, double?, TimeSpan?> report, Action<string> stopRun)
    {
        var e = job.Entry;
        var sw = Stopwatch.StartNew();
        string? chapterTemp = null;
        var staged = false;
        var keepScratch = false;
        try
        {
            // 1. is the source still what the scan saw?
            var fi = new FileInfo(e.FullPath);
            if (!fi.Exists && _storage.UsesScratch && !Directory.Exists(q.MediaFolder))
            {
                var m = "the library is not reachable (is the share mounted?); nothing was changed";
                _log.Write(m + ": " + q.MediaFolder);
                stopRun(m);
                Changed(q, job, JobState.Pending, m);
                return;
            }
            if (!fi.Exists) { Changed(q, job, JobState.Skipped, "not started: the file no longer exists"); _log.Write("skipped (missing): " + e.FullPath); return; }
            if (fi.Length != e.SizeBytes || Math.Abs(fi.LastWriteTimeUtc.Ticks - e.MtimeUtcTicks) > TimeSpan.TicksPerSecond * 2)
            { Changed(q, job, JobState.Skipped, "not started: the file changed since the scan (scan again)"); _log.Write("skipped (changed since scan): " + e.FullPath); return; }
            var srcMtime = fi.LastWriteTimeUtc;
            var srcCtime = PlatformServices.Current.FileTimes.GetCreationUtc(e.FullPath);

            // 2. names
            var dir = Path.GetDirectoryName(e.FullPath)!;
            if (!PathCompare.Equal(job.FinalPath, e.FullPath) && File.Exists(job.FinalPath))
            { Changed(q, job, JobState.Skipped, "not started: the output name is already taken"); _log.Write("skipped (name clash): " + job.FinalPath); return; }
            TryDelete(job.PartPath);

            // 3. scratch mode: copy the source to the local scratch disk; otherwise check free space (the output is written next to the source)
            var encEntry = e;
            var encPart = job.PartPath;
            var workDir = dir;
            if (_storage.UsesScratch)
            {
                Changed(q, job, JobState.CopyingIn, "");
                job.CopyFraction = 0;
                var lastTick = DateTime.UtcNow; var lastFrac = 0.0; var inSw = Stopwatch.StartNew();
                try
                {
                    var st = await _storage.StageAsync(job, q.MediaFolder, f =>
                    {
                        job.CopyFraction = f;
                        job.CopyMBps = inSw.Elapsed.TotalSeconds > 0.5 ? e.SizeBytes * f / 1048576.0 / inSw.Elapsed.TotalSeconds : 0;
                        if (DateTime.UtcNow - lastTick > TimeSpan.FromSeconds(1) && f - lastFrac > 0.0005) { lastTick = DateTime.UtcNow; lastFrac = f; JobChanged?.Invoke(job); report(0, null, null); }
                    }, _killCts!.Token).ConfigureAwait(false);
                    staged = true;
                    encEntry = new ScanEntry
                    {
                        Library = e.Library, RelativePath = e.RelativePath, FullPath = st.Source, SizeBytes = e.SizeBytes,
                        MtimeUtcTicks = e.MtimeUtcTicks, HasExternalSubs = e.HasExternalSubs, Probe = e.Probe,
                    };
                    encPart = st.Part; workDir = st.WorkDir;
                }
                catch (ScratchException sx) when (sx.Problem is ScratchProblem.NoSpace or ScratchProblem.Offline)
                {
                    _log.Write("scratch: " + sx.Message + "; the run is stopped, original untouched: " + e.FullPath);
                    stopRun(sx.Message);
                    Changed(q, job, JobState.Pending, sx.Message);
                    return;
                }
                catch (ScratchException sx) when (sx.Problem == ScratchProblem.TooBig)
                {
                    Changed(q, job, JobState.Skipped, "not started: " + sx.Message);
                    _log.Write("skipped (scratch): " + e.FullPath + ": " + sx.Message);
                    return;
                }
                catch (ScratchException sx)
                {
                    job.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                    Changed(q, job, JobState.Failed, sx.Message);
                    _log.Write("FAILED: " + e.FullPath + ": " + sx.Message);
                    return;
                }
            }
            var needFree = (long)(e.SizeBytes * 1.1) + 1L * 1024 * 1024 * 1024;
            var freeNow = _storage.UsesScratch ? long.MaxValue : _freeBytes(dir);
            if (freeNow < needFree)
            {
                var need = needFree; var free = freeNow;
                var msg = $"not enough free space on {Path.GetPathRoot(dir)} (need about {need / 1073741824.0:0.0} GB, free {free / 1073741824.0:0.0} GB)";
                _log.Write(msg + "; the run is stopped, free some space and press Convert again");
                stopRun(msg);
                Changed(q, job, JobState.Pending, msg);
                return;
            }

            // 4. command
            if (QueueBuilder.NeedsChapterFile(job.Plan, _s))
            {
                var pr = await _probe.RunAsync(encEntry.FullPath, _killCts!.Token).ConfigureAwait(false);
                var chapters = ChapterFile.ParseChapters(pr.Stdout);
                chapterTemp = Path.Combine(workDir, Path.GetFileNameWithoutExtension(job.PartPath) + ".chapters.convertr-part");
                File.WriteAllText(chapterTemp, ChapterFile.Build(chapters, _s.JunkPatterns), new System.Text.UTF8Encoding(false));
            }
            var cmd = FfmpegCommandBuilder.Build(encEntry, job.Plan, _s, encPart, chapterTemp);
            foreach (var n in cmd.Notes) _log.Write("note: " + n);
            _log.Write($"start: {e.FullPath} ({job.Plan.ActionLabel}, {e.SizeBytes / 1048576.0:0} MB)");
            _log.Write("command: " + cmd.ToCommandLine());
            job.CopyFraction = 0;
            Changed(q, job, JobState.Encoding, "");

            // 5. encode
            var control = new ProcessControl();
            lock (_gate) _active.Add(control);
            FfmpegRunResult run;
            try
            {
                var dur = e.Probe.DurationSec;
                var prog = new Progress<ProgressSnapshot>(sn => report(sn.Fraction(dur) * 0.97, sn.Speed, sn.Eta(dur)));
                if (_paused) control.Suspend();
                run = await _ff.RunAsync(cmd.Args, prog, control, _killCts!.Token).ConfigureAwait(false);
            }
            finally { lock (_gate) _active.Remove(control); }

            if (run.Cancelled)
            {
                TryDelete(encPart);
                Changed(q, job, JobState.Pending, "stopped; will start again from the beginning");
                _log.Write("cancelled: partial file deleted, original untouched: " + e.FullPath);
                return;
            }
            if (run.ExitCode != 0)
            {
                TryDelete(encPart);
                var tail = LastLines(run.StderrTail, 3);
                if (tail.Contains("No space left", StringComparison.OrdinalIgnoreCase) || tail.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase))
                {
                    var msg = "the drive ran full while writing; partial file deleted";
                    _log.Write(msg + ": " + e.FullPath);
                    stopRun(msg);
                    Changed(q, job, JobState.Pending, msg);
                    return;
                }
                job.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                Changed(q, job, JobState.Failed, $"ffmpeg failed (exit code {run.ExitCode}): {tail}");
                _log.Write($"FAILED: {e.FullPath}: ffmpeg exit code {run.ExitCode}: {tail}");
                return;
            }

            // 6. verify
            Changed(q, job, JobState.Verifying);
            report(0.97, null, null);
            var outBytes = new FileInfo(encPart).Length;
            var pro = await _probe.RunAsync(encPart, _killCts.Token).ConfigureAwait(false);
            var outInfo = pro.ExitCode == 0 ? ProbeParser.Parse(pro.Stdout, pro.Stderr, pro.ExitCode) : ProbeParser.Failed("ffprobe exit code " + pro.ExitCode + " " + pro.Stderr);
            var vr = Verifier.Verify(e, job.Plan, cmd.Expected, outInfo, outBytes, _s);
            job.OutputBytes = outBytes;
            job.SavingPercent = vr.SavingPercent;
            job.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            job.Speed = e.Probe.DurationSec is > 0 && job.ElapsedSeconds > 0 ? e.Probe.DurationSec.Value / job.ElapsedSeconds : null;
            if (vr.Verdict == VerifyVerdict.Pass && _s.VerifyDecode)
            {
                var bad = await DecodeTestAsync(encPart, outInfo.DurationSec ?? e.Probe.DurationSec ?? 0).ConfigureAwait(false);
                if (bad != null) vr = new VerifyResult(VerifyVerdict.Fail, new[] { "decode test failed: " + bad }, vr.Notes, vr.SavingPercent, vr.SavedBytes);
            }
            if (vr.Verdict == VerifyVerdict.Fail)
            {
                TryDelete(encPart);
                Changed(q, job, JobState.Failed, vr.Summary);
                _log.Write($"FAILED: {e.FullPath}: {vr.Summary}; original untouched, temporary file deleted");
                return;
            }
            if (vr.Verdict == VerifyVerdict.NotWorthIt)
            {
                TryDelete(encPart);
                Changed(q, job, JobState.NotWorthIt, vr.Summary);
                _log.Write($"not worth it: {e.FullPath}: {vr.Summary}; original kept, temporary file deleted");
                return;
            }

            // 6b. scratch mode: only now (verified and smaller) does anything go back to the library, as a temporary file
            if (_storage.UsesScratch)
            {
                job.HoldingPath = HoldingArea.HoldingPathFor(q.MediaFolder, e.FullPath, _s);
                Changed(q, job, JobState.CopyingBack, "");
                job.CopyFraction = 0;
                var lastTick = DateTime.UtcNow; var backSw = Stopwatch.StartNew();
                try
                {
                    await _storage.CopyBackAsync(job, encPart, f =>
                    {
                        job.CopyFraction = f;
                        job.CopyMBps = backSw.Elapsed.TotalSeconds > 0.5 ? outBytes * f / 1048576.0 / backSw.Elapsed.TotalSeconds : 0;
                        if (DateTime.UtcNow - lastTick > TimeSpan.FromSeconds(1)) { lastTick = DateTime.UtcNow; JobChanged?.Invoke(job); report(0.97 + 0.02 * f, null, null); }
                    }, _killCts!.Token).ConfigureAwait(false);
                    // the copy in the library must be a good file too
                    var back = await _probe.RunAsync(job.PartPath, _killCts.Token).ConfigureAwait(false);
                    var backInfo = back.ExitCode == 0 ? ProbeParser.Parse(back.Stdout, back.Stderr, back.ExitCode) : ProbeParser.Failed("ffprobe exit code " + back.ExitCode);
                    if (!string.IsNullOrEmpty(backInfo.ProbeError) || (outInfo.DurationSec is > 0 && backInfo.DurationSec is > 0 && Math.Abs(backInfo.DurationSec.Value - outInfo.DurationSec.Value) > 2))
                    {
                        TryDelete(job.PartPath);
                        throw new ScratchException(ScratchProblem.Copy, "the copy in the library does not play as the local result did; temporary file deleted, original untouched");
                    }
                }
                catch (ScratchException sx) when (sx.Problem is ScratchProblem.NoSpace or ScratchProblem.Offline)
                {
                    TryDelete(job.PartPath);
                    _log.Write("scratch: " + sx.Message + "; the run is stopped, original untouched: " + e.FullPath);
                    stopRun(sx.Message);
                    Changed(q, job, JobState.Pending, sx.Message);
                    return;
                }
                catch (ScratchException sx)
                {
                    TryDelete(job.PartPath);
                    keepScratch = true;
                    Changed(q, job, JobState.Failed, sx.Message);
                    _log.Write("FAILED: " + e.FullPath + ": " + sx.Message);
                    return;
                }
            }

            // 7. swap
            job.HoldingPath = HoldingArea.HoldingPathFor(q.MediaFolder, e.FullPath, _s);
            Changed(q, job, JobState.Swapping);
            try
            {
                await Task.Run(() => _storage.Commit(q.RunId, job, srcMtime, srcCtime, q.MediaFolder)).ConfigureAwait(false);
            }
            catch (SwapException ex)
            {
                TryDelete(job.PartPath);
                Changed(q, job, JobState.Failed, "swap failed: " + ex.Message);
                _log.Write("FAILED: " + e.FullPath + ": swap failed: " + ex.Message);
                return;
            }
            var tsNote = "";
            if (_s.PreserveTimestamps)
                tsNote = Swapper.TimestampMatches(job.FinalPath, srcMtime) ? "; timestamps preserved" : "; WARNING: timestamps differ";
            job.FinishedUtc = DateTime.UtcNow;
            Changed(q, job, JobState.Done, vr.Summary + tsNote);
            _log.Write($"done: {job.FinalPath}: {e.SizeBytes / 1048576.0:0} MB -> {outBytes / 1048576.0:0} MB ({vr.SavingPercent:0.0}% saved), {sw.Elapsed:hh\\:mm\\:ss}{tsNote}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(job.PartPath);
            Changed(q, job, JobState.Pending, "stopped; will start again from the beginning");
            _log.Write("cancelled: " + e.FullPath);
        }
        catch (Exception ex)
        {
            TryDelete(job.PartPath);
            Changed(q, job, JobState.Failed, "unexpected error: " + ex.Message);
            _log.Write("FAILED: " + e.FullPath + ": unexpected error: " + ex);
        }
        finally
        {
            if (chapterTemp != null) TryDelete(chapterTemp);
            if (staged || _storage.UsesScratch)
            {
                if (job.State == JobState.Failed) keepScratch = true;
                _storage.Release(job, keepScratch);
            }
            job.CopyFraction = 0;
        }
    }

    private async Task<string?> DecodeTestAsync(string file, double duration)
    {
        var slices = duration > 120 ? new[] { 0.0, duration * 0.5, Math.Max(0, duration - 25) } : new[] { 0.0 };
        foreach (var start in slices)
        {
            var args = new List<string> { "-v", "error", "-nostdin" };
            if (start > 0) { args.Add("-ss"); args.Add(start.ToString("0.###", CultureInfo.InvariantCulture)); }
            args.AddRange(new[] { "-t", duration > 120 ? "20" : "7200", "-i", file, "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-" });
            var r = await _ff.RunAsync(args, null, null, _killCts!.Token).ConfigureAwait(false);
            if (r.Cancelled) throw new OperationCanceledException();
            var errors = RealDecodeErrors(r.StderrTail);
            if (r.ExitCode != 0 || errors.Length > 0) return LastLines(errors.Length > 0 ? errors : "exit code " + r.ExitCode, 2);
        }
        return null;
    }

    /// <summary>Decode-test messages that mean a damaged file. Timestamp and chapter-reference notes are harmless and ignored.</summary>
    public static string RealDecodeErrors(string stderr)
    {
        var keep = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.IndexOf("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase) < 0
                        && l.IndexOf("Referenced QT chapter track", StringComparison.OrdinalIgnoreCase) < 0
                        && l.IndexOf("timestamps are unset", StringComparison.OrdinalIgnoreCase) < 0);
        return string.Join("\n", keep);
    }

    private static string LastLines(string text, int n)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(n));
    }
}
