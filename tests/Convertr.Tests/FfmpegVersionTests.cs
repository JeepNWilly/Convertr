// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Convertr.Core;
using Xunit;

namespace Convertr.Tests;

public class FfmpegVersionTests
{
    private const string Sample = """
    [
      {"tag_name":"2026-10-15-git-afead44165","prerelease":false,"published_at":"2026-10-15T01:00:00Z","assets":[{"name":"ffmpeg-2026-10-15-git-afead44165-essentials_build.zip","size":1,"browser_download_url":"https://github.com/x/a.zip"}]},
      {"tag_name":"9.0.2","prerelease":false,"published_at":"2026-09-20T01:00:00Z","assets":[
         {"name":"ffmpeg-9.0.2-essentials_build.7z","size":5,"browser_download_url":"https://github.com/x/9.7z"},
         {"name":"ffmpeg-9.0.2-essentials_build.zip","size":100,"browser_download_url":"https://github.com/x/9.zip","digest":"sha256:60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"}]},
      {"tag_name":"8.0.1","prerelease":false,"published_at":"2025-11-20T10:00:00Z","assets":[
         {"name":"ffmpeg-8.0.1-full_build.zip","size":9,"browser_download_url":"https://github.com/x/full.zip"},
         {"name":"ffmpeg-8.0.1-essentials_build.zip","size":106259850,"browser_download_url":"https://github.com/GyanD/codexffmpeg/releases/download/8.0.1/ffmpeg-8.0.1-essentials_build.zip","digest":"sha256:e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673"}]},
      {"tag_name":"7.1.1","prerelease":false,"published_at":"2025-03-12T10:00:00Z","assets":[{"name":"ffmpeg-7.1.1-essentials_build.zip","size":92234348,"browser_download_url":"https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-essentials_build.zip","digest":null}]},
      {"tag_name":"7.0","prerelease":true,"published_at":"2024-04-05T10:00:00Z","assets":[{"name":"ffmpeg-7.0-essentials_build.zip","size":1,"browser_download_url":"https://github.com/x/7.zip"}]},
      {"tag_name":"6.1","prerelease":false,"published_at":"2023-11-11T10:00:00Z","assets":[{"name":"ffmpeg-6.1-full_build.zip","size":1,"browser_download_url":"https://github.com/x/61.zip"}]}
    ]
    """;

    [Fact]
    public void ParseReleases_KeepsOnlyNumberedReleasesWithAnEssentialsZip()
    {
        var v = FfmpegVersions.ParseReleases(Sample);
        Assert.Equal(new[] { "9.0.2", "8.0.1", "7.1.1" }, v.Select(x => x.Version).ToArray());   // no git build, no prerelease, no release without essentials zip
        Assert.All(v, x => Assert.StartsWith("https://", x.Url));
        var a = v.Single(x => x.Version == "8.0.1");
        Assert.Equal(106259850, a.SizeBytes);
        Assert.Equal("e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673", a.Sha256);
        Assert.Equal(new DateTime(2025, 11, 20), a.Published);
        Assert.Null(v.Single(x => x.Version == "7.1.1").Sha256);
    }

    [Fact]
    public void ParseReleases_ToleratesOtherShapes()
    {
        Assert.Empty(FfmpegVersions.ParseReleases("{\"message\":\"rate limit\"}"));
        Assert.Empty(FfmpegVersions.ParseReleases("[]"));
        Assert.Empty(FfmpegVersions.ParseReleases("[{\"tag_name\":\"1.0\"}]"));
    }

    [Fact]
    public void OlderThanLatest_SortsNewestFirst_AndDropsTheLatest()
    {
        var all = FfmpegVersions.ParseReleases(Sample).Concat(new[] { new FfmpegVersion("8.0", "https://x/8.0", 1, null, null), new FfmpegVersion("10.0", "https://x/10", 1, null, null) });
        var older = FfmpegVersions.OlderThanLatest(all);
        Assert.Equal(new[] { "9.0.2", "8.0.1", "8.0", "7.1.1" }, older.Select(v => v.Version).ToArray());
        Assert.True(FfmpegVersions.Compare("10.0", "9.0.2") > 0);
        Assert.True(FfmpegVersions.Compare("8.0", "8.0.1") < 0);
    }

    [Fact]
    public void Fallback_HasTheRequestedVersions_WithGitHubReleaseUrls()
    {
        Assert.Equal(new[] { "8.0.1", "8.0", "7.1.1", "7.0.2", "6.1.1" }, FfmpegVersions.Fallback.Select(v => v.Version).ToArray());
        foreach (var v in FfmpegVersions.Fallback)
        {
            Assert.Equal($"https://github.com/GyanD/codexffmpeg/releases/download/{v.Version}/ffmpeg-{v.Version}-essentials_build.zip", v.Url);
            Assert.True(v.SizeBytes > 50_000_000);
        }
    }

    [Theory]
    [InlineData("9.0.2", "610.00")]
    [InlineData("8.1.2", "610.00")]
    [InlineData("8.1", "610.00")]
    [InlineData("8.0.1", "570.00")]
    [InlineData("8.0", "570.00")]
    [InlineData("7.1.1", "551.76")]
    [InlineData("7.0.2", "551.76")]
    [InlineData("6.1.1", "530.41")]
    [InlineData("6.0", "520.56")]
    [InlineData("5.1.2", "471.41")]
    [InlineData("4.4.1", "456.71")]
    public void RequiredDriver_FollowsTheNvencApiOfTheRelease(string version, string driver) =>
        Assert.Equal(driver, FfmpegVersions.RequiredDriver(version));

    private static HardwareProfile Pascal(string driver = "582.78") => new() { Gpus = { new GpuInfo { Name = "NVIDIA GeForce GTX 1080 Ti", Vendor = "NVIDIA", Driver = driver } } };

    [Fact]
    public void Describe_MarksWhatWorksWithTheDetectedDriver()
    {
        var hw = Pascal();
        Assert.Equal(DriverFit.Works, FfmpegVersions.Describe("8.0.1", hw).Fit);
        Assert.Equal(DriverFit.Works, FfmpegVersions.Describe("7.1.1", hw).Fit);
        var n = FfmpegVersions.Describe("8.1.2", hw);
        Assert.Equal(DriverFit.NeedsNewerDriver, n.Fit);
        Assert.Contains("610", n.Text);
        Assert.Contains("582.78", n.Text);
        Assert.Contains("Known to work", FfmpegVersions.Describe("8.0.1", hw).Text);
        Assert.Contains("older NVIDIA drivers", FfmpegVersions.Describe("8.0.1", hw).Text);
    }

    [Fact]
    public void Describe_WithoutProfileOrNvidiaOrANormalDriverNumber()
    {
        Assert.Equal(DriverFit.Unknown, FfmpegVersions.Describe("8.0.1", null).Fit);
        Assert.Equal(DriverFit.NoNvidia, FfmpegVersions.Describe("8.0.1", new HardwareProfile { Gpus = { new GpuInfo { Name = "Intel UHD", Vendor = "Intel", Driver = "31.0.1" } } }).Fit);
        Assert.Equal(DriverFit.Unknown, FfmpegVersions.Describe("8.0.1", Pascal("32.0.15.8278")).Fit);   // Windows driver number: not comparable
        Assert.Equal(DriverFit.Unknown, FfmpegVersions.Describe("8.0.1", Pascal("")).Fit);
    }

    [Fact]
    public void FolderFor_GivesEachVersionItsOwnFolder_AndIsSafe()
    {
        Assert.Equal(Path.Combine("root", "8.0.1"), FfmpegVersions.FolderFor("root", "8.0.1"));
        Assert.NotEqual(FfmpegVersions.FolderFor("root", "8.0"), FfmpegVersions.FolderFor("root", "8.0.1"));
        Assert.Equal(Path.Combine("root", "_"), FfmpegVersions.FolderFor("root", ".."));
        Assert.Equal(Path.Combine("root", ".._evil"), FfmpegVersions.FolderFor("root", "..\\evil"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> f) { _f = f; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_f(request));
    }

    private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Lookup_ReadsTheOnlineList_AndFiltersIt()
    {
        var pages = new List<string>();
        var h = new Handler(r => { pages.Add(r.RequestUri!.Query); return Json(Sample); });   // 6 items: a single short page
        var res = await FfmpegVersions.LookupAsync(CancellationToken.None, h);
        Assert.True(res.Online);
        Assert.Null(res.Error);
        Assert.Equal(new[] { "8.0.1", "7.1.1" }, res.Versions.Select(v => v.Version).ToArray());   // 9.0.2 is the latest: the normal Download button gets it
        Assert.Single(pages);
    }

    [Fact]
    public async Task Lookup_WhenOffline_ReturnsTheBuiltInList()
    {
        var h = new Handler(_ => throw new HttpRequestException("No such host is known"));
        var res = await FfmpegVersions.LookupAsync(CancellationToken.None, h);
        Assert.False(res.Online);
        Assert.NotNull(res.Error);
        Assert.Equal(FfmpegVersions.Fallback.Count, res.Versions.Count);
        var rate = await FfmpegVersions.LookupAsync(CancellationToken.None, new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.False(rate.Online);
        var junk = await FfmpegVersions.LookupAsync(CancellationToken.None, new Handler(_ => Json("{\"message\":\"x\"}")));
        Assert.False(junk.Online);
    }

    private static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var n in new[] { "ffmpeg-8.0.1-essentials_build/bin/ffmpeg.exe", "ffmpeg-8.0.1-essentials_build/bin/ffprobe.exe" })
                using (var w = new StreamWriter(z.CreateEntry(n).Open())) w.Write("fake " + n);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Download_OfAVersion_GoesToItsOwnFolder_AndChecksTheSha256()
    {
        var bytes = Zip();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var v = new FfmpegVersion("8.0.1", "https://example.test/ffmpeg-8.0.1-essentials_build.zip", bytes.Length, null, sha);
        var root = Fx.TempDir();
        var dest = FfmpegVersions.FolderFor(root, v.Version);
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var r = await FfmpegDownloader.DownloadAsync(dest, false, null, CancellationToken.None, h, new[] { FfmpegVersions.ToSource(v) });
        Assert.Equal(dest, r.Folder);
        Assert.True(FfmpegVersions.IsInstalled(dest));
        Assert.False(FfmpegVersions.IsInstalled(root));   // nothing is placed in the shared folder
        Assert.False(File.Exists(Path.Combine(dest, "download.zip.part")));
    }

    [Fact]
    public async Task Download_WithAWrongSha256OrSize_IsRefused_AndLeavesNothing()
    {
        var bytes = Zip();
        var root = Fx.TempDir();
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var badSha = new FfmpegVersion("8.0.1", "https://example.test/a.zip", bytes.Length, null, new string('0', 64));
        var ex = await Assert.ThrowsAsync<DownloadException>(() => FfmpegDownloader.DownloadAsync(FfmpegVersions.FolderFor(root, "8.0.1"), false, null, CancellationToken.None, h, new[] { FfmpegVersions.ToSource(badSha) }));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(FfmpegVersions.IsInstalled(FfmpegVersions.FolderFor(root, "8.0.1")));
        var badSize = new FfmpegVersion("7.1.1", "https://example.test/b.zip", bytes.Length + 5, null, null);
        var ex2 = await Assert.ThrowsAsync<DownloadException>(() => FfmpegDownloader.DownloadAsync(FfmpegVersions.FolderFor(root, "7.1.1"), false, null, CancellationToken.None, h, new[] { FfmpegVersions.ToSource(badSize) }));
        Assert.Contains("wrong size", ex2.Message);
        Assert.False(FfmpegVersions.IsInstalled(FfmpegVersions.FolderFor(root, "7.1.1")));
    }
}
