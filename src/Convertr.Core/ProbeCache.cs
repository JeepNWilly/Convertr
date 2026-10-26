// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Convertr.Core;

/// <summary>
/// Resumable probe cache (probe_cache.json). Key = full path; an entry is only used when the file's size and
/// modified time still match, so a changed file is probed again. Lives next to the exe, never in the library.
/// </summary>
public sealed class ProbeCache
{
    public const int SchemaVersion = 2;   // 2: stale BPS tags are ignored (build 5), so old cached bitrates are re-probed

    private sealed class Entry
    {
        public long Size { get; set; }
        public long MtimeTicks { get; set; }
        public ProbeInfo Info { get; set; } = new();
    }

    private sealed class FileModel
    {
        public int Schema { get; set; }
        public Dictionary<string, Entry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, Entry> _entries = new(PathCompare.Comparer);
    private readonly object _saveLock = new();
    private readonly string? _path;
    private int _dirty;

    public ProbeCache(string? path) { _path = path; }

    public int Count => _entries.Count;

    public int PendingWrites => Volatile.Read(ref _dirty);

    public static ProbeCache Load(string? path)
    {
        var c = new ProbeCache(path);
        if (path == null || !File.Exists(path)) return c;
        try
        {
            using var fs = File.OpenRead(path);
            var m = JsonSerializer.Deserialize<FileModel>(fs, CompactOptions);
            if (m != null && m.Schema == SchemaVersion)
                foreach (var kv in m.Entries) c._entries[kv.Key] = kv.Value;
        }
        catch { /* a bad cache just means a full rescan */ }
        return c;
    }

    /// <summary>All cached entries (used by the harness to report estimates without touching the library).</summary>
    public IEnumerable<(string Path, long Size, long MtimeTicks, ProbeInfo Info)> Snapshot() =>
        _entries.Select(kv => (kv.Key, kv.Value.Size, kv.Value.MtimeTicks, kv.Value.Info)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string fullPath, long size, long mtimeTicks, out ProbeInfo info)
    {
        if (_entries.TryGetValue(fullPath, out var e) && e.Size == size && e.MtimeTicks == mtimeTicks)
        {
            info = e.Info;
            return true;
        }
        info = null!;
        return false;
    }

    public void Put(string fullPath, long size, long mtimeTicks, ProbeInfo info)
    {
        _entries[fullPath] = new Entry { Size = size, MtimeTicks = mtimeTicks, Info = info };
        Interlocked.Increment(ref _dirty);
    }

    /// <summary>Forget entries under the given roots that were not seen in the latest complete scan (deleted files).</summary>
    public int Prune(IEnumerable<string> roots, ISet<string> seenPaths)
    {
        var removed = 0;
        var rootList = roots.Select(r => r.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).ToList();
        foreach (var key in _entries.Keys.ToList())
        {
            if (seenPaths.Contains(key)) continue;
            if (rootList.Any(r => PathCompare.StartsWith(key, r)) && _entries.TryRemove(key, out _))
            { removed++; Interlocked.Increment(ref _dirty); }
        }
        return removed;
    }

    /// <summary>Atomic save (write a temp file next to the cache, then rename). No-op when there is no path.</summary>
    public void Save()
    {
        if (_path == null) return;
        lock (_saveLock)
        {
            Interlocked.Exchange(ref _dirty, 0);
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            var model = new FileModel { Schema = SchemaVersion, Entries = new Dictionary<string, Entry>(_entries) };
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, model, CompactOptions);
            File.Move(tmp, _path, true);
        }
    }
}
