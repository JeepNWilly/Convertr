// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>Where originals and undone files go. Both areas sit next to the Media folder (same drive, outside it), mirroring its structure.</summary>
public static class HoldingArea
{
    public const string UndoneFolderName = "_Convertr_Undone";

    /// <summary>Folder that is a sibling of the Media folder, for example D:\...\Media\_Convertr_Originals\Movies. A drive root uses a folder inside it.</summary>
    public static string Root(string mediaFolder, string folderName)
    {
        var media = mediaFolder.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(media);
        var leaf = Path.GetFileName(media);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return Path.Combine(mediaFolder, folderName);
        return Path.Combine(parent, folderName, leaf);
    }

    public static string RelativeTo(string mediaFolder, string fullPath)
    {
        var media = mediaFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return PathCompare.StartsWith(fullPath, media) ? fullPath[media.Length..] : Path.GetFileName(fullPath);
    }

    /// <summary>Holding path for an original (mirrors the relative structure). A name that is already taken gets a numeric suffix.</summary>
    public static string HoldingPathFor(string mediaFolder, string originalFullPath, Settings s)
    {
        if (s.Originals == OriginalsHandling.RenameInPlace) return Unique(originalFullPath + ".convertr-orig");
        if (s.Originals == OriginalsHandling.DeleteAfterVerify) return Unique(originalFullPath + ".convertr-del");
        return Unique(Path.Combine(Root(mediaFolder, s.OriginalsFolderName), RelativeTo(mediaFolder, originalFullPath)));
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        for (var i = 1; i < 1000; i++)
        {
            var c = Path.Combine(dir, name + "." + i);
            if (!File.Exists(c)) return c;
        }
        return path + "." + Guid.NewGuid().ToString("N")[..8];
    }
}

public sealed class SwapException : Exception
{
    public SwapException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The swap (DESIGN.md section 5.4): original to the holding area, temporary output into place under the final name, timestamps copied.
/// Every step is journaled first. If a step fails the original is put back, so a failed swap never loses the original.
/// </summary>
public sealed class Swapper
{
    private readonly Settings _s;
    private readonly UndoJournal _journal;
    private readonly RunLog _log;
    private readonly int _retries;
    private readonly TimeSpan _retryDelay;

    public Swapper(Settings s, UndoJournal journal, RunLog log, int retries = 5, TimeSpan? retryDelay = null)
    {
        _s = s;
        _journal = journal;
        _log = log;
        _retries = retries;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
    }

    private void Ev(string runId, string step, string status, QueueJob j, DateTime m, DateTime c, string mediaFolder, string note = "") =>
        _journal.Append(new JournalEvent(runId, DateTime.UtcNow, step, status, j.Entry.FullPath, j.HoldingPath, j.FinalPath, j.Entry.SizeBytes, j.OutputBytes, m, c, mediaFolder, _s.Originals.ToString(), note));

    /// <summary>File locks (for example a media server reading the file) are retried with a pause before giving up.</summary>
    private void Retry(string what, Action act)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { act(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= _retries) throw new SwapException($"{what} failed after {attempt} tries: {ex.Message}", ex);
                _log.Write($"{what} blocked ({ex.Message}); waiting {_retryDelay.TotalSeconds:0} s and trying again ({attempt}/{_retries})");
                if (_retryDelay > TimeSpan.Zero) Thread.Sleep(_retryDelay);
            }
        }
    }

    /// <param name="srcMtimeUtc">Modified time of the original, read before encoding (copied onto the output).</param>
    public void Swap(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder)
    {
        var src = job.Entry.FullPath;
        var final = job.FinalPath;
        var part = job.PartPath;
        var holding = job.HoldingPath;
        if (!File.Exists(part)) throw new SwapException("the converted temporary file is missing: " + part);

        Ev(runId, "Start", "Swapping", job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
        var dir = Path.GetDirectoryName(holding);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(holding)) throw new SwapException("the holding path is already taken: " + holding);

        Retry("moving the original to the holding area", () => File.Move(src, holding));
        Ev(runId, "OriginalMoved", "Swapping", job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
        _log.Write($"original moved: {src} -> {holding}");

        try
        {
            if (File.Exists(final)) throw new SwapException("the final name is already taken: " + final);
            Retry("moving the converted file into place", () => File.Move(part, final));
        }
        catch (Exception ex)
        {
            try { File.Move(holding, src); Ev(runId, "RolledBack", "Failed", job, srcMtimeUtc, srcCtimeUtc, mediaFolder, ex.Message); _log.Write("swap failed, original put back: " + ex.Message); }
            catch (Exception ex2) { _log.Write("swap failed AND the original could not be put back from " + holding + ": " + ex2.Message); throw new SwapException("swap failed and the original is in " + holding + ": " + ex2.Message, ex2); }
            if (ex is SwapException) throw;
            throw new SwapException(ex.Message, ex);
        }
        Ev(runId, "OutputPlaced", "Swapping", job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
        _log.Write($"converted file in place: {final}");

        if (_s.PreserveTimestamps) ApplyTimestamps(final, srcMtimeUtc, srcCtimeUtc);

        if (_s.Originals == OriginalsHandling.DeleteAfterVerify)
        {
            Retry("deleting the original", () => File.Delete(holding));
            Ev(runId, "Done", "OriginalDeleted", job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
            _log.Write("original deleted (user choice): " + src);
        }
        else Ev(runId, "Done", "Done", job, srcMtimeUtc, srcCtimeUtc, mediaFolder);
    }

    public static void ApplyTimestamps(string path, DateTime mtimeUtc, DateTime ctimeUtc)
    {
        if (ctimeUtc > DateTime.MinValue) PlatformServices.Current.FileTimes.SetCreationUtc(path, ctimeUtc);
        File.SetLastWriteTimeUtc(path, mtimeUtc);
    }

    /// <summary>True when the file's modified time is within 2 s of the expected value (exFAT has coarse time stamps).</summary>
    public static bool TimestampMatches(string path, DateTime mtimeUtc) =>
        Math.Abs((File.GetLastWriteTimeUtc(path) - mtimeUtc).TotalSeconds) <= 2;

    /// <summary>
    /// After a crash during Swapping: finish the swap if the original is already in the holding area and the output is available,
    /// otherwise put the original back. Returns true when the job ended as Done.
    /// </summary>
    public bool Recover(string runId, QueueJob job, DateTime srcMtimeUtc, DateTime srcCtimeUtc, string mediaFolder)
    {
        var src = job.Entry.FullPath;
        var holdingHas = File.Exists(job.HoldingPath);
        var finalHas = File.Exists(job.FinalPath);
        var partHas = File.Exists(job.PartPath);
        if (!holdingHas)
        {
            // the original was never moved: nothing was changed
            if (File.Exists(src)) { _log.Write("recovery: swap had not started for " + src + "; the original is intact"); return false; }
            throw new SwapException("recovery: neither the original nor its holding copy exists for " + src);
        }
        if (!finalHas && partHas) File.Move(job.PartPath, job.FinalPath);
        if (File.Exists(job.FinalPath))
        {
            if (_s.PreserveTimestamps) ApplyTimestamps(job.FinalPath, srcMtimeUtc, srcCtimeUtc);
            Ev(runId, "Done", "Done", job, srcMtimeUtc, srcCtimeUtc, mediaFolder, "recovered");
            _log.Write("recovery: swap finished for " + job.FinalPath);
            return true;
        }
        // no output at all: put the original back
        if (!File.Exists(src)) File.Move(job.HoldingPath, src);
        Ev(runId, "RolledBack", "Failed", job, srcMtimeUtc, srcCtimeUtc, mediaFolder, "recovered: rolled back");
        _log.Write("recovery: original put back: " + src);
        return false;
    }
}
