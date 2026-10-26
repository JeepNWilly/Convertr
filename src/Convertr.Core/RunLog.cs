// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text;

namespace Convertr.Core;

/// <summary>Plain-text log, one line per event: [yyyy-MM-dd HH:mm:ss] message. Thread-safe. A null path logs nowhere (tests).</summary>
public sealed class RunLog
{
    private readonly string? _path;
    private readonly object _gate = new();
    public Action<string>? Echo { get; set; }

    public RunLog(string? path) { _path = path; }

    public string? Path => _path;

    public static string NewPath(string logsDir, DateTime now) =>
        System.IO.Path.Combine(logsDir, "Convertr_" + now.ToString("yyyyMMdd_HHmm") + ".log");

    public void Write(string message)
    {
        var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
        Echo?.Invoke(line);
        if (_path == null) return;
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* logging must never stop a run */ }
        }
    }
}
