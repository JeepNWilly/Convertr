// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Reflection;

namespace Convertr;

/// <summary>
/// UI conventions shared by every grid and dialog (project convention): tables and dialogs open with nothing selected
/// and nothing highlighted. Call <see cref="NoInitialSelection"/> for each grid and <see cref="ClearTextSelection"/>
/// for each form that has text boxes.
/// </summary>
public static class GridHelper
{
    public static void Clear(DataGridView g)
    {
        if (g.IsDisposed) return;
        g.ClearSelection();
        try { g.CurrentCell = null; } catch (InvalidOperationException) { /* edit in progress */ }
    }

    /// <summary>Clears selection and CurrentCell on DataBindingComplete and when the form is first shown.</summary>
    public static void NoInitialSelection(DataGridView g, Form owner)
    {
        g.DataBindingComplete += (_, _) => Clear(g);
        g.HandleCreated += (_, _) => g.BeginInvoke(new Action(() => Clear(g)));
        owner.Shown += (_, _) =>
        {
            Clear(g);
            g.BeginInvoke(new Action(() => Clear(g)));
        };
    }

    /// <summary>Removes the "everything selected" highlight a TextBox shows when it first gets focus.</summary>
    public static void ClearTextSelection(Form f)
    {
        f.Shown += (_, _) => f.BeginInvoke(new Action(() => ClearTextSelectionNow(f)));
    }

    public static void ClearTextSelectionNow(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TextBoxBase tb) { tb.SelectionStart = 0; tb.SelectionLength = 0; }
            else if (c is ComboBox cb && cb.DropDownStyle != ComboBoxStyle.DropDownList) { cb.SelectionStart = 0; cb.SelectionLength = 0; }
            if (c.HasChildren) ClearTextSelectionNow(c);
        }
    }

    public static void EnableDoubleBuffer(Control c)
    {
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(c, true);
    }

    /// <summary>Disabled buttons do not raise tooltips in WinForms, so the container shows the tip when the mouse is over one.</summary>
    public static void TipForDisabled(Control container, ToolTip tip, params (Control Button, string Text)[] items)
    {
        Control? last = null;
        container.MouseMove += (_, e) =>
        {
            Control? hit = null;
            foreach (var (b, _) in items)
                if (!b.Enabled && b.Bounds.Contains(e.Location)) { hit = b; break; }
            if (hit == last) return;
            last = hit;
            if (hit == null) { tip.Hide(container); return; }
            var text = items.First(i => i.Button == hit).Text;
            tip.Show(text, container, hit.Left, hit.Bottom + 2, 4000);
        };
        container.MouseLeave += (_, _) => { last = null; tip.Hide(container); };
    }
}
