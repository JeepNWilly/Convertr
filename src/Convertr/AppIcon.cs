// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using System.Reflection;

namespace Convertr;

internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon? Load()
    {
        if (_icon != null) return _icon;
        try
        {
            using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("Convertr.ico");
            if (s != null) _icon = new Icon(s);
        }
        catch { /* the icon is cosmetic */ }
        return _icon;
    }
}
