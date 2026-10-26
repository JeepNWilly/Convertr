// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "convertr-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() { Directory.CreateDirectory(Path); }
    public string File(string rel, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* ignore */ } }
}

public class ThemeResolverTests
{
    [Theory]
    [InlineData(ThemeMode.System, false, false)]
    [InlineData(ThemeMode.System, true, true)]
    [InlineData(ThemeMode.Light, true, false)]
    [InlineData(ThemeMode.Light, false, false)]
    [InlineData(ThemeMode.Dark, false, true)]
    [InlineData(ThemeMode.Dark, true, true)]
    public void IsDark_FollowsTheModeAndWindows(ThemeMode mode, bool systemDark, bool expected) =>
        Assert.Equal(expected, ThemeResolver.IsDark(mode, systemDark));
}

public class SettingsTests
{
    [Fact]
    public void Defaults_MatchTheAcceptedDesignDefaults()
    {
        var s = new Settings();
        Assert.Equal(OutputContainer.Mp4, s.Container);
        Assert.Equal(15, s.KeepIfSmallerPercent);
        Assert.Equal(100, s.KeepIfSmallerMb);
        Assert.False(s.KeepEmbeddedSubsIfNoExternal);        // strip all by default
        Assert.True(s.CleanJunkMetadata);
        Assert.True(s.PreserveTimestamps);
        Assert.False(s.CleanMetadataOnSkipped);
        Assert.False(s.StripSubsOnSkipped);
        Assert.False(s.AddAacCompatTrack);
        Assert.Equal(AudioFallback.Ac3, s.DtsOtherTarget);
        Assert.Equal(OriginalsHandling.MoveToHolding, s.Originals);
        Assert.Equal("_Convertr_Originals", s.OriginalsFolderName);
        Assert.Equal(4000, s.Kbps1080);
        Assert.Equal(2500, s.Kbps720);
        Assert.Equal(1500, s.KbpsSd);
        Assert.Equal(3, s.ProbeThreads);
        Assert.Equal(0, s.BatchLimitGb);
        Assert.Equal("", s.MediaFolder);   // no built-in path: a fresh exe on any computer starts empty
        Assert.Null(s.Libraries);
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.True(s.ProcessInterlaced);
        Assert.False(s.AllowHdr4k);
    }

    [Fact]
    public void RoundTrip_PreservesChanges()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "settings.json");
        var s = new Settings { Container = OutputContainer.Mkv, Mode = EncoderMode.Cpu, Kbps720 = 3000, PreserveTimestamps = false };
        s.MediaFolder = @"D:\TV [x]\Series";
        s.Theme = ThemeMode.Dark;
        s.ProcessInterlaced = false;
        SettingsStore.Save(s, path);
        Assert.Contains("\"Mkv\"", File.ReadAllText(path));      // enums are stored as readable names
        var back = SettingsStore.Load(path);
        Assert.Equal(OutputContainer.Mkv, back.Container);
        Assert.Equal(EncoderMode.Cpu, back.Mode);
        Assert.Equal(3000, back.Kbps720);
        Assert.False(back.PreserveTimestamps);
        Assert.Equal(@"D:\TV [x]\Series", back.MediaFolder);
        Assert.Equal(ThemeMode.Dark, back.Theme);
        Assert.False(back.ProcessInterlaced);
        Assert.DoesNotContain("Libraries", File.ReadAllText(path));   // the legacy list is never written back
    }

    [Theory]
    [InlineData(ThemeMode.System, "\"System\"")]
    [InlineData(ThemeMode.Light, "\"Light\"")]
    [InlineData(ThemeMode.Dark, "\"Dark\"")]
    public void Theme_IsSavedAsAReadableName_AndLoadedBack(ThemeMode mode, string text)
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "settings.json");
        SettingsStore.Save(new Settings { Theme = mode }, path);
        Assert.Contains("\"Theme\": " + text, File.ReadAllText(path));
        Assert.Equal(mode, SettingsStore.Load(path).Theme);
    }

    [Fact]
    public void Theme_MissingInOldFile_DefaultsToSystem()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"Kbps720\":2600}");
        Assert.Equal(ThemeMode.System, SettingsStore.Load(path).Theme);
    }

    [Fact]
    public void OldSettings_UseTheMoviesPathAsTheMediaFolder()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"Libraries\":[{\"Label\":\"Series\",\"Path\":\"D:\\\\TV\"},{\"Label\":\"Movies\",\"Path\":\"Y:\\\\Films\"}],\"Kbps720\":2600}");
        var s = SettingsStore.Load(path);
        Assert.Equal(@"Y:\Films", s.MediaFolder);
        Assert.Equal(2600, s.Kbps720);
        Assert.Null(s.Libraries);
    }

    [Fact]
    public void OldSettings_WithoutMovies_UseTheFirstFolder()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"Libraries\":[{\"Label\":\"\",\"Path\":\"\"},{\"Label\":\"Docs\",\"Path\":\"D:\\\\Docs\"}]}");
        Assert.Equal(@"D:\Docs", SettingsStore.Load(path).MediaFolder);
    }

    [Fact]
    public void OldSettings_WithEmptyLibraries_LeaveTheMediaFolderEmpty()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"Libraries\":[]}");
        Assert.Equal("", SettingsStore.Load(path).MediaFolder);
    }

    [Fact]
    public void NewMediaFolder_WinsOverALeftoverLibrariesList()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"MediaFolder\":\"E:\\\\Series\",\"Libraries\":[{\"Label\":\"Movies\",\"Path\":\"Y:\\\\Films\"}]}");
        Assert.Equal(@"E:\Series", SettingsStore.Load(path).MediaFolder);
    }

    [Fact]
    public void BlankMediaFolder_StaysEmpty()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"MediaFolder\":\"   \"}");
        Assert.Equal("", SettingsStore.Load(path).MediaFolder);
    }

    [Fact]
    public void ToPlannerOptions_CarriesTheInterlacedAndHdrSwitches()
    {
        var o = new Settings { ProcessInterlaced = false, AllowHdr4k = true }.ToPlannerOptions();
        Assert.False(o.ProcessInterlaced);
        Assert.True(o.AllowHdr4k);
    }

    [Fact]
    public void MissingFile_GivesDefaults() =>
        Assert.Equal(OutputContainer.Mp4, SettingsStore.Load("/definitely/not/here/settings.json").Container);

    [Fact]
    public void CorruptFile_GivesDefaults_AndKeepsACopy()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{ this is not json");
        var s = SettingsStore.Load(path);
        Assert.Equal(15, s.KeepIfSmallerPercent);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        using var t = new TempDir();
        var path = t.File("settings.json", "{\"ProbeThreads\":999,\"KeepIfSmallerPercent\":500,\"Kbps720\":-5}");
        var s = SettingsStore.Load(path);
        Assert.Equal(16, s.ProbeThreads);
        Assert.Equal(90, s.KeepIfSmallerPercent);
        Assert.Equal(100, s.Kbps720);
    }

    [Fact]
    public void ToPlannerOptions_CarriesSettings()
    {
        var o = new Settings { Kbps1080 = 5000, Mode = EncoderMode.Cpu, DtsOtherTarget = AudioFallback.Aac, StripSubsOnSkipped = true }.ToPlannerOptions();
        Assert.Equal(5000, o.Kbps1080);
        Assert.Equal(EncoderMode.Cpu, o.Mode);
        Assert.True(o.PreferAacForSurround);
        Assert.True(o.StripSubsOnSkipped);
    }
}

public class CacheTests
{
    [Fact]
    public void Hit_OnlyWhenPathSizeAndMtimeMatch()
    {
        var c = new ProbeCache(null);
        c.Put(@"D:\a.mkv", 100, 5, new ProbeInfo { FormatName = "matroska,webm" });
        Assert.True(c.TryGet(@"D:\a.mkv", 100, 5, out var info));
        Assert.Equal("matroska,webm", info.FormatName);
        Assert.Equal(!PlatformServices.Current.PathsCaseSensitive, c.TryGet(@"d:\A.MKV", 100, 5, out _));   // Windows paths are case-insensitive, Linux paths are not
        Assert.False(c.TryGet(@"D:\a.mkv", 101, 5, out _));      // size changed
        Assert.False(c.TryGet(@"D:\a.mkv", 100, 6, out _));      // modified time changed
        Assert.False(c.TryGet(@"D:\b.mkv", 100, 5, out _));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_IncludingSpecialCharacterPaths()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "probe_cache.json");
        var key = @"D:\Media\Émilie [2001] #1 & 'Co' 日本語\file.mkv";
        var c = new ProbeCache(path);
        c.Put(key, 42, 7, ProbeParser.Parse(Ffp.Json(Ffp.Mkv, 100, 1_000_000, new[] { Ffp.Video("h264", 1280, 720, 900_000), Ffp.Audio("aac", 2) }), null));
        c.Save();
        Assert.False(File.Exists(path + ".tmp"));
        var back = ProbeCache.Load(path);
        Assert.Equal(1, back.Count);
        Assert.True(back.TryGet(key, 42, 7, out var info));
        Assert.Equal(900, info.Video!.Kbps);
    }

    [Fact]
    public void CorruptCache_IsIgnored()
    {
        using var t = new TempDir();
        var p = t.File("probe_cache.json", "garbage");
        Assert.Equal(0, ProbeCache.Load(p).Count);
    }

    [Fact]
    public void Prune_RemovesOnlyMissingFilesUnderScannedRoots()
    {
        var c = new ProbeCache(null);
        var sep = System.IO.Path.DirectorySeparatorChar;
        string P(string a, string b) => a + sep + b;
        c.Put(P("/lib/Movies", "keep.mkv"), 1, 1, new ProbeInfo());
        c.Put(P("/lib/Movies", "gone.mkv"), 1, 1, new ProbeInfo());
        c.Put(P("/lib/Series", "other-root.mkv"), 1, 1, new ProbeInfo());
        var removed = c.Prune(new[] { "/lib/Movies" }, new HashSet<string> { P("/lib/Movies", "keep.mkv") });
        Assert.Equal(1, removed);
        Assert.Equal(2, c.Count);
    }
}

public class LocatorTests
{
    [Fact]
    public void FindsFfprobeInConfiguredFolder_ThenExeFolder_ThenPath()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        using var c = new TempDir();
        var exe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        a.File(exe); b.File(exe); c.File(exe);
        var sep = OperatingSystem.IsWindows() ? ";" : ":";

        Assert.Equal("Setup", FfmpegLocator.Find(a.Path, b.Path, c.Path, "/nonexistent-local")!.Source);
        Assert.Equal("next to Convertr", FfmpegLocator.Find("", b.Path, c.Path, "/nonexistent-local")!.Source);
        using var empty = new TempDir();
        var viaPath = FfmpegLocator.Find(null, empty.Path, "/nonexistent" + sep + c.Path, "/nonexistent-local");
        Assert.Equal("PATH", viaPath!.Source);
        Assert.Equal(c.Path, viaPath.Folder);
    }

    [Fact]
    public void ConfiguredFolderWithoutFfprobe_FallsThrough()
    {
        using var empty = new TempDir();
        using var b = new TempDir();
        b.File(OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        Assert.Equal("next to Convertr", FfmpegLocator.Find(empty.Path, b.Path, "", "/nonexistent-local")!.Source);
    }
}
