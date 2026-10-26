// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

/// <summary>Linux/macOS only: the scanner does not follow symbolic links to folders (they could leave the media folder or loop).</summary>
public class LinuxScannerTests
{
    [Fact]
    public void EnumerateFiles_DoesNotEnterSymlinkedFolders_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows behaviour is unchanged and untested here
        var root = Path.Combine(Path.GetTempPath(), "convertr-link-" + Guid.NewGuid().ToString("N")[..8]);
        var media = Path.Combine(root, "media"); var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(media, "real")); Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(media, "real", "a.mkv"), "x");
            File.WriteAllText(Path.Combine(outside, "secret.mkv"), "x");
            Directory.CreateSymbolicLink(Path.Combine(media, "out"), outside);
            Directory.CreateSymbolicLink(Path.Combine(media, "real", "loop"), media);   // would loop forever if followed
            var files = Scanner.EnumerateFiles(media).Select(f => Path.GetFileName(f.FullPath)).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "a.mkv" }, files);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
