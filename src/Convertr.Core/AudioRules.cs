// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>Audio rules (DESIGN.md 3.4). Pure helpers used by the planner (and by M2's ffmpeg argument builder).</summary>
public static class AudioRules
{
    private static readonly HashSet<string> Compatible = new(StringComparer.OrdinalIgnoreCase) { "aac", "ac3", "eac3", "mp3" };

    private static readonly HashSet<string> LosslessOrDts = new(StringComparer.OrdinalIgnoreCase)
        { "dts", "truehd", "mlp", "flac", "alac", "wavpack" };

    public static bool IsCompatible(AudioInfo a) => Compatible.Contains(a.Codec);

    public static bool IsPcm(AudioInfo a) => a.Codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rule 6 trigger: there are audio tracks, and none is AAC/AC3/EAC3/MP3.</summary>
    public static bool NeedsFix(ProbeInfo p) => p.Audio.Count > 0 && !p.Audio.Any(IsCompatible);

    /// <summary>Audio tracks that would be converted whenever the file is rewritten.</summary>
    public static IEnumerable<AudioInfo> ToConvert(ProbeInfo p) => p.Audio.Where(a => !IsCompatible(a));

    /// <summary>Odd channel counts (3, 4, 5, 7) or an unknown count make the layout ambiguous.</summary>
    public static bool AmbiguousLayout(AudioInfo a) => a.Channels is 0 or 3 or 4 or 5 or 7 || a.Channels > 8;

    /// <summary>Human-readable target for one track that must be converted.</summary>
    public static string TargetFor(AudioInfo a, bool preferAac)
    {
        var heavy = LosslessOrDts.Contains(a.Codec) || IsPcm(a);
        if (a.Channels <= 2) return "AAC 192k";
        if (preferAac) return "AAC 384k";
        if (heavy || a.Channels >= 6) return "AC3 640k";
        return "AAC 384k";
    }
}
