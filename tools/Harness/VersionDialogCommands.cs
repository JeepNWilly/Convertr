// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr;
using Convertr.Core;

namespace Convertr.Harness;

/// <summary>Harness: renders the "Download older" version dialog (Light and Dark) with the online list, and offline with the built-in list; checks the selection rules.</summary>
internal static partial class Program
{
    private static int VersionShots(Dictionary<string, string> o, string outDir)
    {
        ApplicationConfiguration.Initialize();
        var ok = true;
        void Check(bool cond, string what) { Console.WriteLine((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
        var hw = new HardwareProfile { Gpus = { new GpuInfo { Name = "NVIDIA GeForce GTX 1080 Ti", Vendor = "NVIDIA", Driver = "582.78" } } };
        var online = FfmpegVersions.LookupAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"online lookup: online={online.Online} versions={online.Versions.Count} error={online.Error}");
        if (online.Online) Console.WriteLine("  " + string.Join(", ", online.Versions.Select(v => v.Version)));
        Check(online.Online && online.Versions.Count >= 5, "online list has several older versions");
        foreach (var (theme, suffix) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
        {
            Theming.SetMode(theme);
            foreach (var (name, lookup) in new (string, Func<CancellationToken, Task<VersionLookup>>)[]
                     { ("online", ct => Task.FromResult(online)), ("offline", ct => Task.FromResult(new VersionLookup(FfmpegVersions.Fallback, false, "No such host is known"))) })
            {
                using var f = new FfmpegVersionForm(hw, lookup);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(10, 10);
                f.Show(); Pump(1800);
                var grid = AllControls(f).OfType<DataGridView>().First();
                var dl = AllControls(f).OfType<Button>().First(b => b.Text == "Download");
                Check(grid.SelectedRows.Count == 0 && grid.CurrentCell == null, $"[{suffix}/{name}] no row selected at start ({grid.Rows.Count} rows)");
                Check(!dl.Enabled, $"[{suffix}/{name}] Download disabled until a version is picked");
                if (name == "online" && suffix == "light")
                {
                    grid.Rows[Math.Min(1, grid.Rows.Count - 1)].Selected = true; Pump(300);
                    Check(dl.Enabled, $"[{suffix}/{name}] Download enabled after picking a row");
                    grid.ClearSelection(); GridHelper.Clear(grid); Pump(300);
                    Check(!dl.Enabled, $"[{suffix}/{name}] Download disabled again with no selection");
                }
                f.Size = new Size(1000, 440); Pump(300);
                Snap(f, Path.Combine(outDir, $"ffmpeg_older_dialog_{name}_{suffix}.png"));
                if (name == "online") { f.Size = new Size(640, 360); Pump(400); Snap(f, Path.Combine(outDir, $"ffmpeg_older_dialog_small_{suffix}.png")); }
                f.Close();
            }
        }
        Console.WriteLine(ok ? "VERSION DIALOG CHECK OK" : "VERSION DIALOG CHECK FAILED");
        return ok ? 0 : 1;
    }
}
