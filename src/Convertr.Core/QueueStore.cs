// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Convertr.Core;

public enum JobState
{
    Pending,
    Encoding,
    Verifying,
    Swapping,
    Done,
    /// <summary>Verified, but the result was not smaller enough: the original stays, the temporary file is deleted.</summary>
    NotWorthIt,
    Failed,
    /// <summary>Not started on purpose (name clash, file changed since the scan, source missing).</summary>
    Skipped,
    /// <summary>Scratch mode: copying the source from the library to the local scratch disk.</summary>
    CopyingIn,
    /// <summary>Scratch mode: copying the verified result back to the library as a temporary file.</summary>
    CopyingBack,
}

/// <summary>One file in a run. Holds everything needed to rebuild the ffmpeg command after a restart.</summary>
public sealed class QueueJob
{
    public int Id { get; set; }
    public JobState State { get; set; } = JobState.Pending;
    public ScanEntry Entry { get; set; } = new();
    public PlanItem Plan { get; set; } = new();
    public string FinalPath { get; set; } = "";
    public string PartPath { get; set; } = "";
    public string HoldingPath { get; set; } = "";
    public string Message { get; set; } = "";
    public long OutputBytes { get; set; }
    public double SavingPercent { get; set; }
    public double ElapsedSeconds { get; set; }
    public double? Speed { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Scratch mode: progress (0..1) and speed (MB/s) of the copy that is running. Not saved.</summary>
    [JsonIgnore] public double CopyFraction { get; set; }
    [JsonIgnore] public double CopyMBps { get; set; }

    [JsonIgnore]
    public bool IsFinal => State is JobState.Done or JobState.NotWorthIt or JobState.Failed or JobState.Skipped;
}

public sealed class QueueFile
{
    public int Schema { get; set; } = 1;
    public string RunId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string MediaFolder { get; set; } = "";
    public bool DryRun { get; set; }
    public List<QueueJob> Jobs { get; set; } = new();

    [JsonIgnore]
    public int Remaining => Jobs.Count(j => !j.IsFinal);
}

/// <summary>queue.json: written after every step (atomic temp + rename), so a crash or reboot can resume the run.</summary>
public static class QueueStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    public static string DefaultPath => PlatformServices.Current.Paths.QueuePath;

    public static void Save(QueueFile q, string path)
    {
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(q, Opts));
            File.Move(tmp, path, true);
        }
    }

    /// <summary>Loads a saved queue; missing or unreadable gives null (an unreadable file is kept as .bad).</summary>
    public static QueueFile? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<QueueFile>(File.ReadAllText(path), Opts);
        }
        catch
        {
            try { File.Copy(path, path + ".bad", true); } catch { /* ignore */ }
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
