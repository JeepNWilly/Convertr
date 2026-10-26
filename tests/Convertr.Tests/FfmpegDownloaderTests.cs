// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Drawing;
using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class FfmpegDownloaderTests
{
    private static string MakeZip(string dir, bool withProbe = true, string root = "ffmpeg-9.0.2-essentials_build")
    {
        var path = Path.Combine(dir, "test.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(text); }
        Add(root + "/bin/ffmpeg.exe", "fake ffmpeg");
        if (withProbe) Add(root + "/bin/ffprobe.exe", "fake ffprobe");
        Add(root + "/bin/ffplay.exe", "fake ffplay");
        Add(root + "/LICENSE", "GPL text");
        Add(root + "/doc/ffmpeg.html", "docs");
        Add("../evil.exe", "must never be written outside");
        return path;
    }

    [Fact]
    public void Extract_TakesOnlyFfmpegFfprobeAndLicence_IntoTheFolder()
    {
        var dir = Fx.TempDir();
        var dest = Path.Combine(dir, "ffmpeg");
        var written = FfmpegDownloader.ExtractTools(MakeZip(dir), dest);
        Assert.Contains("ffmpeg.exe", written);
        Assert.Contains("ffprobe.exe", written);
        Assert.Equal("fake ffmpeg", File.ReadAllText(Path.Combine(dest, "ffmpeg.exe")));
        Assert.Equal("fake ffprobe", File.ReadAllText(Path.Combine(dest, "ffprobe.exe")));
        Assert.False(File.Exists(Path.Combine(dest, "ffplay.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "evil.exe")));
        Assert.Empty(Directory.GetFiles(dest, "*.part"));
    }

    [Fact]
    public void Extract_ReplacesAnOlderCopy()
    {
        var dir = Fx.TempDir();
        var dest = Path.Combine(dir, "ffmpeg");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "ffmpeg.exe"), "old");
        FfmpegDownloader.ExtractTools(MakeZip(dir), dest);
        Assert.Equal("fake ffmpeg", File.ReadAllText(Path.Combine(dest, "ffmpeg.exe")));
    }

    [Fact]
    public void Extract_ArchiveWithoutFfprobe_IsRefused()
    {
        var dir = Fx.TempDir();
        var ex = Assert.Throws<DownloadException>(() => FfmpegDownloader.ExtractTools(MakeZip(dir, withProbe: false), Path.Combine(dir, "x")));
        Assert.Contains("ffprobe.exe", ex.Message);
    }

    [Fact]
    public void ChooseFolder_UsesThePreferredOne_WhenItIsWritable()
    {
        var dir = Fx.TempDir();
        Assert.Equal(Path.Combine(dir, "a"), FfmpegDownloader.ChooseWritableFolder(Path.Combine(dir, "a"), Path.Combine(dir, "b")));
        Assert.True(Directory.Exists(Path.Combine(dir, "a")));
    }

    [Fact]
    public void ChooseFolder_FallsBack_WhenThePreferredOneCannotBeCreated()
    {
        var dir = Fx.TempDir();
        var blocker = Path.Combine(dir, "afile");
        File.WriteAllText(blocker, "x");     // a file where the folder should be: cannot create a folder below it
        var fallback = Path.Combine(dir, "fallback");
        Assert.Equal(fallback, FfmpegDownloader.ChooseWritableFolder(Path.Combine(blocker, "ffmpeg"), fallback));
        Assert.True(Directory.Exists(fallback));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request));
        }
    }

    private static FfmpegSource Src(string name, string url, bool sha = false) => new(name, (_, _) => Task.FromResult(url), sha);

    [Fact]
    public async Task Download_Extracts_ReportsProgress_AndChecksSha256()
    {
        var dir = Fx.TempDir();
        var zipBytes = File.ReadAllBytes(MakeZip(dir));
        var sha = Convert.ToHexString(SHA256.HashData(zipBytes));
        var h = new FakeHandler
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith(".sha256")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha + "  ffmpeg-release-essentials.zip") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) },
        };
        var dest = Path.Combine(dir, "out");
        var reports = new List<DownloadProgress>();
        var res = await FfmpegDownloader.DownloadAsync(dest, false, new Progress<DownloadProgress>(reports.Add), CancellationToken.None, h,
            new[] { Src("one", "https://example.test/ffmpeg.zip", sha: true) });
        await Task.Delay(50);
        Assert.Equal(Path.Combine(dest, "ffmpeg.exe"), res.FfmpegPath);
        Assert.True(File.Exists(res.FfprobePath));
        Assert.Contains(h.Urls, u => u.EndsWith(".sha256"));
        Assert.Contains(reports, r => r.Stage == "Downloading" && r.Bytes > 0);
        Assert.False(File.Exists(Path.Combine(dest, "download.zip.part")));
    }

    [Fact]
    public async Task Download_WithWrongSha256_IsRejected_AndNothingIsLeftBehind()
    {
        var dir = Fx.TempDir();
        var zipBytes = File.ReadAllBytes(MakeZip(dir));
        var h = new FakeHandler
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith(".sha256")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('0', 64)) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) },
        };
        var dest = Path.Combine(dir, "out");
        var ex = await Assert.ThrowsAsync<DownloadException>(() => FfmpegDownloader.DownloadAsync(dest, false, null, CancellationToken.None, h, new[] { Src("one", "https://example.test/f.zip", sha: true) }));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(Path.Combine(dest, "ffmpeg.exe")));
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task Download_FirstSourceFails_FallbackIsUsed()
    {
        var dir = Fx.TempDir();
        var zipBytes = File.ReadAllBytes(MakeZip(dir));
        var h = new FakeHandler
        {
            Respond = r => r.RequestUri!.Host == "first.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) },
        };
        var res = await FfmpegDownloader.DownloadAsync(Path.Combine(dir, "o"), false, null, CancellationToken.None, h,
            new[] { Src("first", "https://first.test/a.zip"), Src("second", "https://second.test/b.zip") });
        Assert.Equal("second", res.Source);
        Assert.Equal(new[] { "https://first.test/a.zip", "https://second.test/b.zip" }, h.Urls);
    }

    [Fact]
    public async Task Download_Offline_GivesAClearMessage()
    {
        var dir = Fx.TempDir();
        var h = new FakeHandler { Respond = _ => throw new HttpRequestException("No such host is known.", new System.Net.Sockets.SocketException()) };
        var ex = await Assert.ThrowsAsync<DownloadException>(() => FfmpegDownloader.DownloadAsync(Path.Combine(dir, "o"), false, null, CancellationToken.None, h, new[] { Src("one", "https://x.test/a.zip"), Src("two", "https://y.test/b.zip") }));
        Assert.Contains("no internet connection", ex.Message);
        Assert.Contains("one:", ex.Message);
        Assert.Contains("two:", ex.Message);
    }

    [Fact]
    public async Task Download_Cancel_StopsAndDeletesThePartialFile()
    {
        var dir = Fx.TempDir();
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler { Respond = _ => { cts.Cancel(); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) }; } };
        var dest = Path.Combine(dir, "o");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FfmpegDownloader.DownloadAsync(dest, false, null, cts.Token, h, new[] { Src("one", "https://x.test/a.zip") }));
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task Sources_AreTheOfficialOnes_AndTheOlderOneIs8_0_1()
    {
        var latest = FfmpegDownloader.LatestSources();
        Assert.Equal(3, latest.Count);
        Assert.Equal(FfmpegDownloader.GyanLatest, await latest[0].ResolveUrl(new HttpClient(), default));
        Assert.True(latest[0].HasSha256);
        var older = await FfmpegDownloader.OlderSources().Single().ResolveUrl(new HttpClient(), default);
        Assert.Contains("8.0.1", older);
    }

    [Fact]
    public void FriendlyError_CoversOfflineTimeoutAndDisk()
    {
        Assert.Contains("no internet", FfmpegDownloader.FriendlyError(new HttpRequestException("x", new System.Net.Sockets.SocketException())));
        Assert.Contains("404", FfmpegDownloader.FriendlyError(new HttpRequestException("x", null, HttpStatusCode.NotFound)));
        Assert.Contains("timed out", FfmpegDownloader.FriendlyError(new TaskCanceledException()));
        Assert.Contains("not a valid zip", FfmpegDownloader.FriendlyError(new InvalidDataException()));
    }
}

public class FfmpegLocatorExtraTests
{
    [Fact]
    public void FindsTheFfmpegSubfolderNextToTheExe_AndTheLocalAppDataCopy()
    {
        var exeDir = Fx.TempDir();
        var local = Fx.TempDir();
        var empty = Fx.TempDir();
        var probe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        Directory.CreateDirectory(Path.Combine(exeDir, "ffmpeg"));
        File.WriteAllText(Path.Combine(exeDir, "ffmpeg", probe), "x");
        var a = FfmpegLocator.Find("", exeDir, "", local)!;
        Assert.Equal("ffmpeg folder next to Convertr", a.Source);
        Assert.Equal(Path.Combine(exeDir, "ffmpeg"), a.Folder);

        Directory.CreateDirectory(Path.Combine(local, "Convertr", "ffmpeg"));
        File.WriteAllText(Path.Combine(local, "Convertr", "ffmpeg", probe), "x");
        var b = FfmpegLocator.Find("", empty, "", local)!;
        Assert.Contains("local app data", b.Source);
    }

    [Fact]
    public void NvencProblem_ExplainsTheDriverVersionMismatch()
    {
        var msg = FfmpegLocator.NvencProblem("[hevc_nvenc @ 0000] Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0\n[hevc_nvenc @ 0000] The minimum required Nvidia driver for nvenc is 610.00 or newer");
        Assert.Contains("13.1", msg);
        Assert.Contains("13.0", msg);
        Assert.Contains("CPU mode", msg);
        Assert.Contains("driver 610 or newer", msg);
        Assert.Contains("GTX 10 series", msg);
        Assert.Contains("no NVENC-capable", FfmpegLocator.NvencProblem("No NVENC capable devices found"));
    }
}

public class DialogGeometryTests
{
    private static readonly Rectangle Wa = new(0, 0, 1920, 1040);

    [Fact]
    public void CentersOverTheOwner()
    {
        var r = DialogGeometry.Compute(new Size(900, 700), new Rectangle(100, 100, 1380, 800), Wa);
        Assert.Equal(new Rectangle(340, 150, 900, 700), r);
    }

    [Fact]
    public void ClampsInsideTheWorkingArea_WhenTheOwnerIsNearAnEdge()
    {
        var r = DialogGeometry.Compute(new Size(900, 700), new Rectangle(1500, 700, 400, 300), Wa);
        Assert.True(r.Right <= Wa.Right && r.Bottom <= Wa.Bottom && r.Left >= Wa.Left && r.Top >= Wa.Top);
        var r2 = DialogGeometry.Compute(new Size(900, 700), new Rectangle(-300, -200, 400, 300), Wa);
        Assert.Equal(0, r2.Left);
        Assert.Equal(0, r2.Top);
    }

    [Fact]
    public void ADialogTallerThanTheScreen_IsShrunkToFit()
    {
        var r = DialogGeometry.Compute(new Size(1000, 1300), new Rectangle(0, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(1040, r.Height);
        Assert.Equal(0, r.Top);
        Assert.Equal(1000, r.Width);
    }

    [Fact]
    public void WorksOnASecondMonitorWithANegativeOrigin()
    {
        var wa2 = new Rectangle(-1920, 0, 1920, 1040);
        var r = DialogGeometry.Compute(new Size(900, 700), new Rectangle(-1800, 50, 800, 600), wa2);
        Assert.True(r.Left >= wa2.Left && r.Right <= wa2.Right);
    }

    [Fact]
    public void RegressionSetupOpenedTooLow_TallDialogOverCenteredOwner_StartsAtTheTopOfTheWorkArea()
    {
        // Setup is about 900 high; owner is 1380x800 starting 60 px from the top of a 1080p screen
        var r = DialogGeometry.Compute(new Size(1000, 1100), new Rectangle(270, 60, 1380, 800), Wa);
        Assert.Equal(0, r.Top);
        Assert.Equal(1040, r.Height);
    }
}
