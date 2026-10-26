// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using System.Runtime.InteropServices;
using Convertr.Core;
using Microsoft.Win32;

namespace Convertr;

/// <summary>Colours for one theme.</summary>
public sealed class Palette
{
    public Color Back, Fore, Input, InputFore, Button, Border, Grid, Lines, Header, Muted, Good, Bad, Accent, AccentFore, SelBack, SelFore;

    public static readonly Palette Light = new()
    {
        Back = SystemColors.Control, Fore = SystemColors.ControlText, Input = SystemColors.Window, InputFore = SystemColors.WindowText,
        Button = SystemColors.Control, Border = Color.FromArgb(173, 173, 173), Grid = SystemColors.Window, Lines = Color.FromArgb(230, 230, 230),
        Header = Color.FromArgb(234, 234, 234), Muted = SystemColors.GrayText, Good = Color.FromArgb(40, 90, 40), Bad = Color.Firebrick,
        Accent = Color.FromArgb(23, 136, 139), AccentFore = Color.White, SelBack = SystemColors.Highlight, SelFore = SystemColors.HighlightText,
    };

    public static readonly Palette Dark = new()
    {
        Back = Color.FromArgb(32, 33, 36), Fore = Color.FromArgb(232, 232, 232), Input = Color.FromArgb(43, 45, 49), InputFore = Color.FromArgb(240, 240, 240),
        Button = Color.FromArgb(51, 54, 59), Border = Color.FromArgb(74, 77, 82), Grid = Color.FromArgb(38, 40, 43), Lines = Color.FromArgb(58, 61, 66),
        Header = Color.FromArgb(51, 54, 59), Muted = Color.FromArgb(154, 160, 166), Good = Color.FromArgb(127, 203, 138), Bad = Color.FromArgb(255, 128, 128),
        Accent = Color.FromArgb(43, 179, 179), AccentFore = Color.FromArgb(30, 30, 30), SelBack = Color.FromArgb(31, 111, 114), SelFore = Color.White,
    };
}

/// <summary>
/// Dark / Light / System theme for every window. System follows the Windows "app mode" (AppsUseLightTheme) and updates live
/// when Windows changes it. Forms call <see cref="Register"/> at the end of their constructor; changing the mode re-applies
/// to every open form immediately (colours, native scroll bars and the title bar).
/// </summary>
public static class Theming
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }
    public static Palette Colors => IsDark ? Palette.Dark : Palette.Light;
    public static event Action? Changed;

    private static bool _hooked;
    private static SynchronizationContext? _ui;

    static Theming() { IsDark = Resolve(Mode); }

    /// <summary>True when Windows apps should be dark (HKCU ...\Themes\Personalize\AppsUseLightTheme = 0).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static bool Resolve(ThemeMode m) => ThemeResolver.IsDark(m, m == ThemeMode.System && SystemPrefersDark());

    /// <summary>Sets the mode and re-themes every registered window right away.</summary>
    public static void SetMode(ThemeMode m)
    {
        Mode = m;
        Refresh();
    }

    public static void Refresh()
    {
        IsDark = Resolve(Mode);
        try
        {
            ToolStripManager.Renderer = IsDark ? new ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false } : new ToolStripProfessionalRenderer();
        }
        catch { /* renderer is cosmetic */ }
        Changed?.Invoke();
    }

    private static void EnsureHooks()
    {
        if (_hooked) return;
        _hooked = true;
        _ui = SynchronizationContext.Current;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (Mode != ThemeMode.System) return;
                if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
                void Go() { if (Mode == ThemeMode.System && Resolve(Mode) != IsDark) Refresh(); }
                Form? owner = null;
                try { owner = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.IsHandleCreated && !x.IsDisposed); } catch { /* collection changed */ }
                if (owner != null) { try { owner.BeginInvoke(new Action(Go)); return; } catch { /* closing */ } }
                _ui?.Post(_ => Go(), null);
            };
        }
        catch { /* no system events available */ }
    }

    /// <summary>Themes a form now and again whenever it is shown or the theme changes. Call at the end of the constructor.</summary>
    public static void Register(Form f)
    {
        EnsureHooks();
        void Do() { if (!f.IsDisposed) Apply(f); }
        Action h = Do;
        f.HandleCreated += (_, _) => Do();
        f.Shown += (_, _) => Do();
        Changed += h;
        f.FormClosed += (_, _) => Changed -= h;
        Do();
    }

    /// <summary>Owner-draws a ToolTip in dark mode and restores the native look in light mode.</summary>
    public static void Track(ToolTip tip)
    {
        void Do()
        {
            try
            {
                tip.OwnerDraw = IsDark;
                if (!IsDark) return;
                tip.BackColor = Palette.Dark.Input;
                tip.ForeColor = Palette.Dark.Fore;
            }
            catch { /* disposed */ }
        }
        tip.Popup += (_, e) =>
        {
            if (!tip.OwnerDraw) return;
            var text = tip.GetToolTip(e.AssociatedControl!);
            var sz = TextRenderer.MeasureText(text, SystemFonts.StatusFont, new Size(420, 0), TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(sz.Width + 12, sz.Height + 10);
        };
        tip.Draw += (_, e) =>
        {
            using var b = new SolidBrush(Palette.Dark.Input);
            using var pen = new Pen(Palette.Dark.Border);
            e.Graphics.FillRectangle(b, e.Bounds);
            e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, SystemFonts.StatusFont, new Rectangle(6, 5, e.Bounds.Width - 10, e.Bounds.Height - 8),
                Palette.Dark.Fore, TextFormatFlags.WordBreak);
        };
        Action h = Do;
        Changed += h;
        tip.Disposed += (_, _) => Changed -= h;
        Do();
    }

    // ---------------------------------------------------------------- applying

    public static void Apply(Form f)
    {
        var p = Colors;
        f.SuspendLayout();
        try
        {
            f.BackColor = p.Back;
            f.ForeColor = p.Fore;
            ApplyChildren(f, p);
        }
        finally { f.ResumeLayout(false); }
        SetTitleBar(f, IsDark);
        f.Invalidate(true);
    }

    /// <summary>Re-applies the colours to one control (for example a label whose Tag changed).</summary>
    public static void Apply(Control c) => ApplyTo(c, Colors);

    private static void ApplyChildren(Control parent, Palette p)
    {
        foreach (Control c in parent.Controls) ApplyTo(c, p);
    }

    private static void ApplyTo(Control c, Palette p)
    {
        var dark = IsDark;
        switch (c)
        {
            case Button b:
                if (dark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    b.BackColor = p.Button;
                    b.ForeColor = p.Fore;
                    b.FlatAppearance.BorderColor = p.Border;
                    b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(p.Button, 0.15f);
                    b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(p.Button, 0.05f);
                }
                else
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.ResetBackColor();
                    b.UseVisualStyleBackColor = true;
                    b.ForeColor = p.Fore;
                }
                return;
            case TextBoxBase t:
                t.BackColor = p.Input;
                t.ForeColor = p.InputFore;
                SetBorder(t, dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D);
                NativeTheme(t, dark);
                return;
            case ComboBox cb:
                cb.BackColor = p.Input;
                cb.ForeColor = p.InputFore;
                cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                NativeTheme(cb, dark);
                return;
            case NumericUpDown n:
                n.BackColor = p.Input;
                n.ForeColor = p.InputFore;
                n.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                foreach (Control k in n.Controls) NativeTheme(k, dark);
                return;
            case DateTimePicker d:
                d.CalendarMonthBackground = p.Input;
                d.CalendarForeColor = p.InputFore;
                d.CalendarTitleBackColor = p.Header;
                d.CalendarTitleForeColor = p.Fore;
                d.BackColor = p.Input;
                d.ForeColor = p.InputFore;
                return;
            case RadioButton r:
                r.BackColor = p.Back;
                r.ForeColor = p.Fore;
                r.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                if (dark) { r.FlatAppearance.BorderColor = p.Border; r.FlatAppearance.CheckedBackColor = p.Input; }
                return;
            case CheckBox k:
                k.BackColor = p.Back;
                k.ForeColor = p.Fore;
                k.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                if (dark) { k.FlatAppearance.BorderColor = p.Border; k.FlatAppearance.CheckedBackColor = p.Input; }
                return;
            case GroupBox g:
                g.BackColor = p.Back;
                g.ForeColor = p.Fore;
                g.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                ApplyChildren(g, p);
                return;
            case Label l:
                l.ForeColor = (l.Tag as string) switch { "muted" => p.Muted, "good" => p.Good, "bad" => p.Bad, _ => p.Fore };
                return;
            case DataGridView grid:
                ApplyGrid(grid, p);
                return;
            case ProgressBar pb:
                pb.BackColor = p.Input;
                pb.ForeColor = p.Accent;
                NativeTheme(pb, dark);
                return;
            case ToolStrip ts:
                ts.BackColor = p.Back;
                ts.ForeColor = p.Fore;
                return;
            default:
                c.BackColor = p.Back;
                c.ForeColor = p.Fore;
                if (c.HasChildren) ApplyChildren(c, p);
                return;
        }
    }

    private static void SetBorder(TextBoxBase t, BorderStyle s)
    {
        if (t.BorderStyle != s) t.BorderStyle = s;
    }

    private static void ApplyGrid(DataGridView g, Palette p)
    {
        var dark = IsDark;
        g.BackgroundColor = p.Grid;
        g.GridColor = dark ? p.Lines : Palette.Light.Lines;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.DefaultCellStyle.BackColor = p.Grid;
        g.DefaultCellStyle.ForeColor = p.InputFore;
        g.DefaultCellStyle.SelectionBackColor = p.SelBack;
        g.DefaultCellStyle.SelectionForeColor = p.SelFore;
        g.AlternatingRowsDefaultCellStyle.BackColor = p.Grid;
        g.AlternatingRowsDefaultCellStyle.ForeColor = p.InputFore;
        g.EnableHeadersVisualStyles = !dark;
        if (dark)
        {
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.ColumnHeadersDefaultCellStyle.BackColor = p.Header;
            g.ColumnHeadersDefaultCellStyle.ForeColor = p.Fore;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Header;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Fore;
        }
        else
        {
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Raised;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.Empty;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.Empty;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.Empty;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Empty;
        }
        NativeTheme(g, dark);
        foreach (Control k in g.Controls) NativeTheme(k, dark);
        g.Invalidate();
    }

    /// <summary>Colour of the Action column text for the current theme.</summary>
    public static Color ActionColor(PlanAction a) => IsDark
        ? a switch
        {
            PlanAction.Error => Color.FromArgb(255, 128, 128),
            PlanAction.Review => Color.FromArgb(240, 168, 72),
            PlanAction.ReencodeVideo => Color.FromArgb(120, 180, 255),
            PlanAction.FixAudioOnly => Color.FromArgb(205, 160, 235),
            PlanAction.Remux => Color.FromArgb(100, 205, 205),
            _ => Color.FromArgb(150, 154, 160),
        }
        : a switch
        {
            PlanAction.Error => Color.Firebrick,
            PlanAction.Review => Color.FromArgb(176, 92, 0),
            PlanAction.ReencodeVideo => Color.FromArgb(0, 78, 150),
            PlanAction.FixAudioOnly => Color.FromArgb(110, 50, 140),
            PlanAction.Remux => Color.FromArgb(0, 110, 110),
            _ => Color.FromArgb(100, 100, 100),
        };

    public static Color ErrorRowColor => IsDark ? Color.FromArgb(255, 128, 128) : Color.Firebrick;

    // ---------------------------------------------------------------- native bits

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static void NativeTheme(Control c, bool dark)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : null, null); } catch { /* older Windows */ }
    }

    /// <summary>Dark (or light) title bar via DWM immersive dark mode (Windows 10 1809+ / Windows 11).</summary>
    public static void SetTitleBar(Form f, bool dark)
    {
        if (!f.IsHandleCreated) return;
        try
        {
            var v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
            // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED: repaint the frame
            SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }
        catch { /* cosmetic */ }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Bg = Palette.Dark.Input, Hot = Color.FromArgb(62, 66, 72), Line = Palette.Dark.Border;
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuItemSelected => Hot;
        public override Color MenuItemSelectedGradientBegin => Hot;
        public override Color MenuItemSelectedGradientEnd => Hot;
        public override Color MenuItemBorder => Line;
        public override Color MenuBorder => Line;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color MenuStripGradientBegin => Palette.Dark.Back;
        public override Color MenuStripGradientEnd => Palette.Dark.Back;
        public override Color MenuItemPressedGradientBegin => Hot;
        public override Color MenuItemPressedGradientEnd => Hot;
    }
}
