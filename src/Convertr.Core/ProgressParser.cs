// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;

namespace Convertr.Core;

/// <summary>One progress report from ffmpeg's "-progress pipe:1" output.</summary>
public sealed record ProgressSnapshot(double OutSeconds, double? Speed, double? Fps, long TotalSize, bool End)
{
    /// <summary>Fraction 0..1 of a file of the given duration (0 when the duration is unknown).</summary>
    public double Fraction(double? durationSec) => durationSec is > 0 ? Math.Clamp(OutSeconds / durationSec.Value, 0, 1) : 0;

    /// <summary>Estimated time left for a file of the given duration, or null when the speed is unknown.</summary>
    public TimeSpan? Eta(double? durationSec)
    {
        if (durationSec is not > 0 || Speed is not > 0.0001) return null;
        var left = Math.Max(0, durationSec.Value - OutSeconds) / Speed.Value;
        return TimeSpan.FromSeconds(left);
    }
}

/// <summary>Feeds on the key=value lines ffmpeg prints with "-progress pipe:1" and emits a snapshot at every "progress=" line.</summary>
public sealed class ProgressParser
{
    private double _out;
    private double? _speed, _fps;
    private long _size;

    /// <summary>Returns a snapshot when the line completes a block ("progress=continue" or "progress=end"), otherwise null.</summary>
    public ProgressSnapshot? Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var i = line.IndexOf('=');
        if (i <= 0) return null;
        var key = line[..i].Trim();
        var val = line[(i + 1)..].Trim();
        switch (key)
        {
            case "out_time_us":
            case "out_time_ms":   // despite its name, ffmpeg writes microseconds here too
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0) _out = us / 1_000_000.0;
                break;
            case "out_time":
                if (_out <= 0 && TimeSpan.TryParse(val, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero) _out = t.TotalSeconds;
                break;
            case "speed":
                _speed = double.TryParse(val.TrimEnd('x', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) ? sp : null;
                break;
            case "fps":
                _fps = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
                break;
            case "total_size":
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz)) _size = sz;
                break;
            case "progress":
                return new ProgressSnapshot(_out, _speed, _fps, _size, val == "end");
        }
        return null;
    }
}
