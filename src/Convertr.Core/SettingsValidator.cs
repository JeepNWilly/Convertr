// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>
/// Start-up check of the saved settings against THIS computer, so that paths and measurements that belong to another computer are not
/// shown as if they were valid. It only clears fields in memory (the caller saves); it never deletes the settings file or any user data.
/// </summary>
public static class SettingsValidator
{
    /// <summary>
    /// - FFmpeg folder: cleared when it does not hold ffprobe on this computer (the normal auto-locate then applies, or "ffmpeg not found").
    /// - Media folder: cleared when it does not exist AND the settings were saved by another computer (or by an older version that did not
    ///   record the computer). On the same computer a missing folder is kept, because a network drive can simply be offline.
    /// - Hardware measurements (benchmark speeds) belong to the computer that measured them and are cleared when the profile is from another one.
    /// Returns a short sentence for every change, for the start-up notice.
    /// </summary>
    public static List<string> ApplyForThisPc(Settings s, string machineName, Func<string, bool> folderExists, Func<string, bool> ffmpegFolderValid)
    {
        var notes = new List<string>();
        var same = !string.IsNullOrWhiteSpace(s.SavedOnMachine) && string.Equals(s.SavedOnMachine.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(s.MediaFolder) && !same && !Safe(folderExists, s.MediaFolder))
        {
            notes.Add($"The saved Media folder ({s.MediaFolder}) does not exist on this computer, so it was cleared. Choose your Media folder.");
            s.MediaFolder = "";
        }
        if (!string.IsNullOrWhiteSpace(s.FfmpegFolder) && !Safe(ffmpegFolderValid, s.FfmpegFolder))
        {
            notes.Add($"The saved FFmpeg folder ({s.FfmpegFolder}) is not valid on this computer, so it was cleared (Convertr looks for ffmpeg by itself).");
            s.FfmpegFolder = "";
        }
        if (s.Hardware != null && !string.IsNullOrWhiteSpace(s.Hardware.MachineName) &&
            !string.Equals(s.Hardware.MachineName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            s.BenchmarkCpuX = null; s.BenchmarkGpuX = null; s.BenchmarkUtc = null;
            notes.Add("The saved hardware profile is from another computer; the hardware is detected again.");
        }
        return notes;
    }

    private static bool Safe(Func<string, bool> f, string path)
    {
        try { return f(path); } catch { return false; }
    }
}
