// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;

namespace Convertr.Core;

/// <summary>Where a dialog goes: centered over the main window, clamped fully inside the monitor's working area, shrunk if it is too big.</summary>
public static class DialogGeometry
{
    /// <summary>Pure geometry (unit tested): the bounds for a dialog of the given size.</summary>
    public static Rectangle Compute(Size dialog, Rectangle ownerBounds, Rectangle workingArea)
    {
        var w = Math.Min(dialog.Width, workingArea.Width);
        var h = Math.Min(dialog.Height, workingArea.Height);
        var x = ownerBounds.Left + (ownerBounds.Width - w) / 2;
        var y = ownerBounds.Top + (ownerBounds.Height - h) / 2;
        x = Math.Max(workingArea.Left, Math.Min(x, workingArea.Right - w));
        y = Math.Max(workingArea.Top, Math.Min(y, workingArea.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    /// <summary>
    /// Size for a resizable dialog: the remembered size when there is one (both values above zero), otherwise the default; never smaller
    /// than the minimum and never larger than the working area (the minimum itself is limited to the working area on tiny screens).
    /// </summary>
    public static Size ResolveSize(Size defaultSize, int savedWidth, int savedHeight, Size minimum, Rectangle workingArea)
    {
        var w = savedWidth > 0 && savedHeight > 0 ? savedWidth : defaultSize.Width;
        var h = savedWidth > 0 && savedHeight > 0 ? savedHeight : defaultSize.Height;
        var minW = Math.Min(minimum.Width, workingArea.Width);
        var minH = Math.Min(minimum.Height, workingArea.Height);
        return new Size(Math.Min(Math.Max(w, minW), workingArea.Width), Math.Min(Math.Max(h, minH), workingArea.Height));
    }
}
