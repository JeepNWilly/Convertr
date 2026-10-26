// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Drawing;
using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class ResizableSetupTests
{
    private static readonly Rectangle Wa = new(0, 0, 1920, 1040);
    private static readonly Size Min = new(560, 420);

    [Fact]
    public void NoSavedSize_UsesTheDefault()
    {
        Assert.Equal(new Size(1100, 900), DialogGeometry.ResolveSize(new Size(1100, 900), 0, 0, Min, Wa));
    }

    [Fact]
    public void SavedSize_Wins_ButIsKeptBetweenTheMinimumAndTheScreen()
    {
        Assert.Equal(new Size(900, 700), DialogGeometry.ResolveSize(new Size(1100, 900), 900, 700, Min, Wa));
        Assert.Equal(new Size(560, 420), DialogGeometry.ResolveSize(new Size(1100, 900), 100, 100, Min, Wa));
        Assert.Equal(new Size(1920, 1040), DialogGeometry.ResolveSize(new Size(1100, 900), 5000, 4000, Min, Wa));
    }

    [Fact]
    public void OnATinyScreen_TheMinimumGivesWay()
    {
        var r = DialogGeometry.ResolveSize(new Size(1100, 900), 0, 0, Min, new Rectangle(0, 0, 500, 380));
        Assert.Equal(new Size(500, 380), r);
    }

    [Fact]
    public void SavedSize_RoundTripsAndBadValuesAreDropped()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "s.json");
        SettingsStore.Save(new Settings { SetupWidth = 1000, SetupHeight = 700 }, path);
        var back = SettingsStore.Load(path);
        Assert.Equal((1000, 700), (back.SetupWidth, back.SetupHeight));
        var bad = new Settings { SetupWidth = 50, SetupHeight = 700 };
        bad.Normalize();
        Assert.Equal((0, 0), (bad.SetupWidth, bad.SetupHeight));
    }
}

public class PortabilityTests
{
    private static readonly Func<string, bool> None = _ => false;
    private static readonly Func<string, bool> All = _ => true;

    [Fact]
    public void FreshSettings_HaveNoMachineSpecificPaths()
    {
        var s = new Settings();
        Assert.Equal("", s.MediaFolder);
        Assert.Equal("", s.FfmpegFolder);
        Assert.Null(s.Hardware);
        Assert.Null(FfmpegLocator.Find("", exeFolder: "/nonexistent/exe", pathEnv: "", localAppDataFolder: "/nonexistent/local"));
    }

    [Fact]
    public void NoSettingsFile_MeansEmptyMediaFolder()
    {
        using var t = new TempDir();
        Assert.Equal("", SettingsStore.Load(System.IO.Path.Combine(t.Path, "missing.json")).MediaFolder);
    }

    [Fact]
    public void SettingsFromAnotherComputer_MissingMediaFolderIsCleared()
    {
        var s = new Settings { MediaFolder = @"D:\Media", SavedOnMachine = "OLD-PC" };
        var notes = SettingsValidator.ApplyForThisPc(s, "NEW-PC", None, All);
        Assert.Equal("", s.MediaFolder);
        Assert.Single(notes);
    }

    [Fact]
    public void SettingsWithoutAMachineName_AreTreatedAsForeign()
    {
        var s = new Settings { MediaFolder = @"D:\Media" };
        SettingsValidator.ApplyForThisPc(s, "NEW-PC", None, All);
        Assert.Equal("", s.MediaFolder);
    }

    [Fact]
    public void ExistingMediaFolder_IsKeptEvenFromAnotherComputer()
    {
        var s = new Settings { MediaFolder = @"D:\Media", SavedOnMachine = "OLD-PC" };
        Assert.Empty(SettingsValidator.ApplyForThisPc(s, "NEW-PC", All, All));
        Assert.Equal(@"D:\Media", s.MediaFolder);
    }

    [Fact]
    public void SameComputer_KeepsAMissingMediaFolder_BecauseANetworkDriveCanBeOffline()
    {
        var s = new Settings { MediaFolder = @"D:\Media", SavedOnMachine = "windows-pc" };
        var notes = SettingsValidator.ApplyForThisPc(s, "WINDOWS-PC", None, All);
        Assert.Equal(@"D:\Media", s.MediaFolder);
        Assert.Empty(notes);
    }

    [Fact]
    public void InvalidFfmpegFolder_IsClearedOnAnyComputer()
    {
        var s = new Settings { FfmpegFolder = @"C:\gone", SavedOnMachine = "PC" };
        var notes = SettingsValidator.ApplyForThisPc(s, "PC", All, None);
        Assert.Equal("", s.FfmpegFolder);
        Assert.Single(notes);
        var ok = new Settings { FfmpegFolder = @"C:\ffmpeg\bin", SavedOnMachine = "PC" };
        Assert.Empty(SettingsValidator.ApplyForThisPc(ok, "PC", All, All));
        Assert.Equal(@"C:\ffmpeg\bin", ok.FfmpegFolder);
    }

    [Fact]
    public void ThrowingExistenceCheck_CountsAsMissing()
    {
        var s = new Settings { MediaFolder = @"Q:\x", SavedOnMachine = "A" };
        SettingsValidator.ApplyForThisPc(s, "B", _ => throw new IOException("boom"), All);
        Assert.Equal("", s.MediaFolder);
    }

    [Fact]
    public void HardwareProfileFromAnotherComputer_ClearsBenchmarks()
    {
        var s = new Settings { SavedOnMachine = "PC", BenchmarkCpuX = 1.2, BenchmarkGpuX = 9, BenchmarkUtc = DateTime.UtcNow, Hardware = new HardwareProfile { MachineName = "OLD" } };
        var notes = SettingsValidator.ApplyForThisPc(s, "NEW", All, All);
        Assert.Null(s.BenchmarkCpuX);
        Assert.Null(s.BenchmarkGpuX);
        Assert.Single(notes);
    }

    [Fact]
    public void Save_StampsTheComputerName_AndNeverDeletesTheFile()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "s.json");
        SettingsStore.Save(new Settings { MediaFolder = "E:\\M" }, path);
        Assert.Equal(Environment.MachineName, SettingsStore.Load(path).SavedOnMachine);
        var s = SettingsStore.Load(path);
        SettingsValidator.ApplyForThisPc(s, "OTHER", None, None);
        Assert.True(System.IO.File.Exists(path));
    }

    private sealed class FakeProbe : IHardwareProbe
    {
        private readonly string _cpuJson;
        public FakeProbe(string cpuJson) { _cpuJson = cpuJson; }
        public Task<string> RunAsync(string exe, string[] args, CancellationToken ct) => Task.FromResult(_cpuJson);
    }

    [Fact]
    public async Task ProfileWithMachineName_ComparesTheName()
    {
        var saved = new HardwareProfile { MachineName = "OLD" };
        Assert.True(await HardwareDetector.ProfileIsFromAnotherComputerAsync(saved, new FakeProbe(""), "NEW"));
        Assert.False(await HardwareDetector.ProfileIsFromAnotherComputerAsync(saved, new FakeProbe(""), "old"));
    }
}
