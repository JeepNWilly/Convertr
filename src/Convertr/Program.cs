// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;

namespace Convertr;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ReportCrash(Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Convertr-crash.log");
            File.AppendAllText(path, $"{DateTime.Now:s} {ex}\r\n\r\n");
        }
        catch { /* nothing more we can do */ }
        try { MessageBox.Show("Convertr hit an unexpected error:\r\n\r\n" + ex?.Message, "Convertr", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { /* ignore */ }
    }
}
