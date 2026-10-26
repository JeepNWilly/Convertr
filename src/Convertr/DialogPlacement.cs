// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr.Core;

namespace Convertr;

/// <summary>
/// Places every dialog (Setup, Details, run summary, Undo, pattern editor) centered over the main window and keeps it fully on the
/// screen: the position is clamped to the working area of the monitor that holds the main window, and a dialog taller or wider than
/// that area is shrunk to fit (the Setup window scrolls inside).
/// </summary>
public static class DialogPlacement
{
    /// <summary>Centers the form over its owner now. Call after the form has its final size (and again in Shown).</summary>
    public static void CenterOverOwner(Form f)
    {
        Rectangle owner;
        Rectangle wa;
        var o = f.Owner;
        if (o != null && o.WindowState != FormWindowState.Minimized)
        {
            owner = o.Bounds;
            wa = Screen.FromControl(o).WorkingArea;
        }
        else
        {
            var scr = Screen.FromPoint(Cursor.Position);
            wa = scr.WorkingArea;
            owner = wa;
        }
        var r = DialogGeometry.Compute(f.Size, owner, wa);
        if (r.Size != f.Size) f.Size = r.Size;
        f.Location = r.Location;
    }

    /// <summary>Hooks Load and Shown so the dialog opens centered over the main window and stays on screen. Call at the end of the constructor.</summary>
    public static void Attach(Form f)
    {
        f.StartPosition = FormStartPosition.CenterParent;   // initial guess; corrected after the final size is known
        f.Load += (_, _) => CenterOverOwner(f);
        f.Shown += (_, _) => CenterOverOwner(f);
    }
}
