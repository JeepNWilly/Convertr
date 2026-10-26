// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>
/// Where one conversion job's files live and how its result is committed (Convertr_svc DESIGN.md section 7A).
/// <see cref="InPlaceStorage"/> is the original behaviour: the temporary output sits next to the source and the swap happens in place.
/// A scratch-disk implementation (copy in, encode locally, copy back) is added later for NAS-backed libraries.
/// </summary>
public interface IJobStorage
{
    string Name { get; }

    /// <summary>Commits a verified output: original to the holding area, output into place, timestamps copied. Journaled; a failure puts the original back.</summary>
    void Commit(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder);

    /// <summary>After a crash during Swapping: finish or roll back. True when the job ended as Done.</summary>
    bool Recover(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder);

    /// <summary>True when the encode happens on a local scratch disk (copy in, encode, copy back).</summary>
    bool UsesScratch => false;

    /// <summary>Makes the source available for encoding and tells where the encoder writes. In place: the source itself and the part file next to it.</summary>
    Task<StagedJob> StageAsync(QueueJob job, string mediaFolder, Action<double> progress, CancellationToken ct) =>
        Task.FromResult(new StagedJob(job.Entry.FullPath, job.PartPath, Path.GetDirectoryName(job.Entry.FullPath) ?? ""));

    /// <summary>Scratch only: puts the verified local result next to the original as <see cref="QueueJob.PartPath"/> (the swap then renames it).</summary>
    Task CopyBackAsync(QueueJob job, string localPart, Action<double> progress, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Frees what <see cref="StageAsync"/> reserved (scratch copy and budget). <paramref name="keep"/> keeps the files for inspection after a failure.</summary>
    void Release(QueueJob job, bool keep) { }

    /// <summary>Start of a run: removes leftovers of earlier runs that no job owns any more.</summary>
    void CleanStale() { }
}

/// <summary>Where the encoder reads and writes for one job.</summary>
public sealed record StagedJob(string Source, string Part, string WorkDir);

public enum ScratchProblem
{
    /// <summary>Settings are not usable (folder missing, not marked, library and originals on different mounts).</summary>
    Config,
    /// <summary>Not enough room on the scratch disk right now: stop the run, keep the job pending.</summary>
    NoSpace,
    /// <summary>The library (NAS) cannot be reached: keep the job pending.</summary>
    Offline,
    /// <summary>The job can never fit the configured scratch budget / per-job cap: skipped.</summary>
    TooBig,
    /// <summary>A copy failed or did not verify: the job fails, the original is untouched.</summary>
    Copy,
}

public sealed class ScratchException : Exception
{
    public ScratchProblem Problem { get; }
    public ScratchException(ScratchProblem problem, string message, Exception? inner = null) : base(message, inner) { Problem = problem; }
}

/// <summary>Today's behaviour, unchanged: <see cref="Swapper"/> does the work on the same volume as the source.</summary>
public sealed class InPlaceStorage : IJobStorage
{
    private readonly Swapper _swapper;
    public InPlaceStorage(Swapper swapper) { _swapper = swapper; }
    public string Name => "in-place";
    public void Commit(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder) =>
        _swapper.Swap(runId, job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
    public bool Recover(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder) =>
        _swapper.Recover(runId, job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
}
