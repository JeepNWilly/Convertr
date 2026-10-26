// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr;
using Convertr.Core;

namespace Convertr.Harness;

/// <summary>Harness: renders the resizable Setup window at several sizes (Light and Dark) and checks the layout. Reads and writes nothing in any library.</summary>
internal static partial class Program
{
    private static int SetupShots(Dictionary<string, string> o, string outDir)
    {
        ApplicationConfiguration.Initialize();
        var ok = true;
        void Check(bool cond, string what) { Console.WriteLine((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Console.WriteLine($"working area {wa.Width}x{wa.Height}");
        foreach (var (theme, suffix) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
        {
            Theming.SetMode(theme);
            var settings = new Settings { Theme = theme };
            if (o.TryGetValue("ffmpeg", out var ff)) settings.FfmpegFolder = ff;
            foreach (var (name, w, h) in new[] { ("default", 0, 0), ("large", 1500, 1000), ("small", 760, 520), ("min", 1, 1) })
            {
                using var f = new SetupForm(settings);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(10, 10);
                f.Show(); Pump(2500);
                if (name == "default") Console.WriteLine($"[{suffix}] default window {f.Width}x{f.Height} min {f.MinimumSize.Width}x{f.MinimumSize.Height}");
                var before = TextBoxWidth(f, "ffmpeg / ffprobe folder");
                if (name != "default") { f.Size = name == "min" ? f.MinimumSize : new Size(Math.Min(w, wa.Width), Math.Min(h, wa.Height)); Pump(600); }
                var after = TextBoxWidth(f, "ffmpeg / ffprobe folder");
                Console.WriteLine($"[{suffix}] {name}: window {f.Width}x{f.Height}, ffmpeg path box {before} -> {after}");
                if (name == "large") Check(after > before + 100, $"[{suffix}] path box stretches with width ({before} -> {after})");
                var bar = AllControls(f).OfType<FlowLayoutPanel>().First(x => x.Dock == DockStyle.Bottom && x.Controls.OfType<Button>().Any());
                var saveBtn = bar.Controls.OfType<Button>().First(b => b.Text == "Cancel");   // the right-most button
                var pt = f.PointToClient(saveBtn.PointToScreen(Point.Empty));
                Check(f.ClientSize.Width - (pt.X + saveBtn.Width) < 30 && f.ClientSize.Height - (pt.Y + saveBtn.Height) < 20, $"[{suffix}] {name}: Cancel (right-most button) pinned bottom-right (gap {f.ClientSize.Width - (pt.X + saveBtn.Width)}, {f.ClientSize.Height - (pt.Y + saveBtn.Height)})");
                var scroll = AllControls(f).OfType<Panel>().First(p => p.AutoScroll);
                Console.WriteLine($"[{suffix}] {name}: scrollbars vertical={scroll.VerticalScroll.Visible} horizontal={scroll.HorizontalScroll.Visible}");
                if (name is "small" or "min") Check(scroll.VerticalScroll.Visible, $"[{suffix}] {name}: content scrolls vertically");
                foreach (Control c in AllControls(f)) if (c is TextBox t) Check(t.SelectionLength == 0, $"[{suffix}] {name}: text box {t.Name}{t.Text.Length} not selected");
                Check(f.ActiveControl is Button, $"[{suffix}] {name}: no text control active");
                Snap(f, Path.Combine(outDir, $"setup_resizable_{name}_{suffix}.png"));
                f.Close();
            }
        }

        // remembered size: restored (clamped) and saved back
        Theming.SetMode(ThemeMode.Light);
        var rs = new Settings { SetupWidth = 1200, SetupHeight = 800 };
        using (var f = new SetupForm(rs))
        {
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(10, 10);
            f.Show(); Pump(1500);
            Check(f.Width == Math.Min(1200, wa.Width) && f.Height == Math.Min(800, wa.Height), $"remembered size restored: {f.Width}x{f.Height}");
            f.Size = new Size(1000, 700); Pump(300);
            f.DialogResult = DialogResult.OK; f.Close(); Pump(300);
            Check(f.Result.SetupWidth == 1000 && f.Result.SetupHeight == 700, $"size saved with Save: {f.Result.SetupWidth}x{f.Result.SetupHeight}");
        }
        var huge = new Settings { SetupWidth = 9000, SetupHeight = 9000 };
        using (var f = new SetupForm(huge))
        {
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(10, 10);
            f.Show(); Pump(1500);
            Check(f.Width <= wa.Width && f.Height <= wa.Height, $"oversized remembered size clamped to screen: {f.Width}x{f.Height}");
            f.Close();
        }
        Console.WriteLine(ok ? "SETUP LAYOUT CHECK OK" : "SETUP LAYOUT CHECK FAILED");
        return ok ? 0 : 1;
    }

    private static int TextBoxWidth(Form f, string rowLabel)
    {
        var lbl = AllControls(f).OfType<Label>().First(l => l.Text == rowLabel);
        var tlp = (TableLayoutPanel)lbl.Parent!;
        var cell = tlp.GetControlFromPosition(1, tlp.GetPositionFromControl(lbl).Row);
        return cell?.Width ?? -1;
    }
}
