// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>One converted file that can be (or cannot be) undone.</summary>
public sealed record UndoItem(
    string RunId, string JournalFile, string OriginalPath, string HoldingPath, string NewPath, long OrigSize, long NewSize,
    DateTime OrigMtimeUtc, DateTime OrigCtimeUtc, string MediaFolder, string Mode, bool CanUndo, string Status, bool OriginalDeleted = false);

/// <summary>
/// Undo (DESIGN.md section 5.7): the converted file is moved to the "undone" area (never deleted), the original is
/// moved back and its timestamps restored. A file is only moved back if its original place is free. Originals that were purged or
/// deleted cannot be restored. Everything is journaled and logged.
/// </summary>
public static class UndoService
{
    public static List<UndoItem> Load(IEnumerable<string> journalFiles)
    {
        var items = new List<UndoItem>();
        foreach (var file in journalFiles.OrderByDescending(f => f))
        {
            var last = new Dictionary<string, JournalEvent>(PathCompare.Comparer);
            foreach (var e in UndoJournal.Read(file)) last[e.OriginalPath] = e;
            foreach (var e in last.Values.OrderBy(x => x.OriginalPath))
            {
                if (e.Step != "Done") continue;   // not finished, rolled back or already undone
                var deleted = e.Status == "OriginalDeleted";
                var (can, status) = Check(deleted, e.OriginalPath, e.HoldingPath, e.NewPath);
                items.Add(new UndoItem(e.RunId, file, e.OriginalPath, e.HoldingPath, e.NewPath, e.OrigSize, e.NewSize, e.OrigMtimeUtc, e.OrigCtimeUtc, e.MediaFolder, e.Mode, can, status, deleted));
            }
        }
        return items;
    }

    public static (bool Can, string Status) Check(bool originalDeleted, string originalPath, string holdingPath, string newPath)
    {
        if (originalDeleted) return (false, "Cannot undo (original was deleted)");
        if (!File.Exists(newPath)) return (false, "Cannot undo (converted file is missing)");
        if (!File.Exists(holdingPath)) return (false, "Cannot undo (original purged or missing)");
        // Same name before and after (for example mp4 to mp4): the converted file itself sits in the original's place; it is moved away first.
        var samePlace = PathCompare.Equal(Path.GetFullPath(originalPath), Path.GetFullPath(newPath));
        if (File.Exists(originalPath) && !samePlace) return (false, "Cannot undo (original place is taken)");
        return (true, "Can undo");
    }

    /// <summary>Where the converted file goes when it is undone.</summary>
    public static string UndonePathFor(UndoItem i)
    {
        if (i.Mode == nameof(OriginalsHandling.RenameInPlace)) return WithSuffix(i.NewPath + ".convertr-undone");
        var media = i.MediaFolder.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(media);
        var leaf = Path.GetFileName(media);
        var rel = HoldingArea.RelativeTo(i.MediaFolder, i.NewPath);
        var root = string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)
            ? Path.Combine(i.MediaFolder, HoldingArea.UndoneFolderName)
            : Path.Combine(parent, HoldingArea.UndoneFolderName, leaf);
        return WithSuffix(Path.Combine(root, rel));
    }

    private static string WithSuffix(string p) => HoldingArea.Unique(p);

    public sealed record UndoResult(UndoItem Item, bool Success, string Message);

    public static List<UndoResult> Execute(IEnumerable<UndoItem> items, UndoJournal journal, RunLog log, bool restoreTimestamps = true)
    {
        var results = new List<UndoResult>();
        foreach (var raw in items)
        {
            var (can, status) = Check(raw.OriginalDeleted, raw.OriginalPath, raw.HoldingPath, raw.NewPath);
            if (!can) { log.Write("undo skipped: " + raw.OriginalPath + " - " + status); results.Add(new UndoResult(raw, false, status)); continue; }
            var undone = UndonePathFor(raw);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(undone)!);
                File.Move(raw.NewPath, undone);
                try { File.Move(raw.HoldingPath, raw.OriginalPath); }
                catch { File.Move(undone, raw.NewPath); throw; }
                if (restoreTimestamps && raw.OrigMtimeUtc > DateTime.MinValue) Swapper.ApplyTimestamps(raw.OriginalPath, raw.OrigMtimeUtc, raw.OrigCtimeUtc);
                journal.Append(new JournalEvent(raw.RunId, DateTime.UtcNow, "Undone", "Undone", raw.OriginalPath, raw.HoldingPath, undone, raw.OrigSize, raw.NewSize, raw.OrigMtimeUtc, raw.OrigCtimeUtc, raw.MediaFolder, raw.Mode, "undone"));
                // also mark it in the run's own journal so the next Load no longer offers it
                if (journal.Path != raw.JournalFile)
                    new UndoJournal(raw.JournalFile).Append(new JournalEvent(raw.RunId, DateTime.UtcNow, "Undone", "Undone", raw.OriginalPath, raw.HoldingPath, undone, raw.OrigSize, raw.NewSize, raw.OrigMtimeUtc, raw.OrigCtimeUtc, raw.MediaFolder, raw.Mode, "undone"));
                log.Write($"undone: {raw.OriginalPath} restored; converted file kept at {undone}");
                results.Add(new UndoResult(raw, true, "restored; converted file kept at " + undone));
            }
            catch (Exception ex)
            {
                log.Write("undo failed for " + raw.OriginalPath + ": " + ex.Message);
                results.Add(new UndoResult(raw, false, "failed: " + ex.Message));
            }
        }
        return results;
    }
}
