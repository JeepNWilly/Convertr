// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;

namespace Convertr.Core;

/// <summary>One line of the undo journal. The journal is append-only: the last event for a file tells where it stands.</summary>
public sealed record JournalEvent(
    string RunId, DateTime TimeUtc, string Step, string Status, string OriginalPath, string HoldingPath, string NewPath,
    long OrigSize, long NewSize, DateTime OrigMtimeUtc, DateTime OrigCtimeUtc, string MediaFolder, string Mode, string Note);

/// <summary>
/// Undo journal (a CSV per run, Logs\undo_yyyyMMdd_HHmm.csv). Each step of a swap is written BEFORE it happens ("Start") and after it
/// ("OriginalMoved", "OutputPlaced", "Done"), so a crash can be recovered or rolled back.
/// </summary>
public sealed class UndoJournal
{
    public static readonly string[] Header =
    {
        "RunId", "TimeUtc", "Step", "Status", "OriginalPath", "HoldingPath", "NewPath", "OrigSize", "NewSize", "OrigMtimeUtc", "OrigCtimeUtc", "MediaFolder", "Mode", "Note",
    };

    private readonly string? _path;
    private readonly object _gate = new();

    public UndoJournal(string? path) { _path = path; }

    public string? Path => _path;

    public static string NewPath(string logsDir, DateTime now) => System.IO.Path.Combine(logsDir, "undo_" + now.ToString("yyyyMMdd_HHmm") + ".csv");

    private static string D(DateTime d) => d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public void Append(JournalEvent e)
    {
        if (_path == null) return;
        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var exists = File.Exists(_path);
            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var w = new StreamWriter(fs, new UTF8Encoding(!exists));
            if (!exists) w.Write(Csv.Line(Header) + "\r\n");
            w.Write(Csv.Line(new[]
            {
                e.RunId, D(e.TimeUtc), e.Step, e.Status, e.OriginalPath, e.HoldingPath, e.NewPath,
                e.OrigSize.ToString(CultureInfo.InvariantCulture), e.NewSize.ToString(CultureInfo.InvariantCulture), D(e.OrigMtimeUtc), D(e.OrigCtimeUtc),
                e.MediaFolder, e.Mode, e.Note,
            }) + "\r\n");
            w.Flush();
            fs.Flush(true);
        }
    }

    public static List<JournalEvent> Read(string path)
    {
        var list = new List<JournalEvent>();
        if (!File.Exists(path)) return list;
        var rows = Csv.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (rows.Count < 2) return list;
        var h = rows[0];
        int Ix(string n) => h.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        foreach (var r in rows.Skip(1))
        {
            string F(string n) { var i = Ix(n); return i >= 0 && i < r.Count ? r[i] : ""; }
            DateTime T(string n) => DateTime.TryParse(F(n), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;
            long L(string n) => long.TryParse(F(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            list.Add(new JournalEvent(F("RunId"), T("TimeUtc"), F("Step"), F("Status"), F("OriginalPath"), F("HoldingPath"), F("NewPath"),
                L("OrigSize"), L("NewSize"), T("OrigMtimeUtc"), T("OrigCtimeUtc"), F("MediaFolder"), F("Mode"), F("Note")));
        }
        return list;
    }
}
