// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertr.Core;

/// <summary>A place to download from. ExpectedSha256 / ExpectedSize come from the release list when it has them (GitHub publishes a digest for newer releases).</summary>
public sealed record FfmpegSource(string Name, Func<HttpClient, CancellationToken, Task<string>> ResolveUrl, bool HasSha256, string? ExpectedSha256 = null, long? ExpectedSize = null);

public sealed record DownloadProgress(string Stage, long Bytes, long? Total, string Source)
{
    public double? Fraction => Total is > 0 ? Math.Min(1.0, (double)Bytes / Total.Value) : null;
}

public sealed record DownloadResult(string Folder, string FfmpegPath, string FfprobePath, string Source, string Url);

public sealed class DownloadException : Exception
{
    public DownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// "Download FFmpeg" (Setup): the official free Windows build from gyan.dev (essentials zip, SHA-256 checked), with the GitHub mirror
/// of the same builds and BtbN's GitHub builds as fallbacks. Only ffmpeg.exe, ffprobe.exe and the licence text are extracted.
/// </summary>
public static class FfmpegDownloader
{
    public const string GyanLatest = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string OlderVersion = "8.0.1";

    /// <summary>"Latest release" sources, in the order they are tried.</summary>
    public static IReadOnlyList<FfmpegSource> LatestSources() => new List<FfmpegSource>
    {
        new("gyan.dev (release essentials)", (_, _) => Task.FromResult(GyanLatest), true),
        new("GitHub mirror of gyan.dev builds", async (http, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/GyanD/codexffmpeg/releases/latest");
            req.Headers.UserAgent.ParseAdd("Convertr");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? throw new DownloadException("no release tag");
            return $"https://github.com/GyanD/codexffmpeg/releases/download/{tag}/ffmpeg-{tag}-essentials_build.zip";
        }, false),
        new("BtbN GitHub builds", (_, _) => Task.FromResult("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"), false),
    };

    /// <summary>The old fixed 8.0.1 source (the Setup list now offers several versions) for NVIDIA drivers that are too old for the newest ffmpeg's NVENC.</summary>
    public static IReadOnlyList<FfmpegSource> OlderSources() => new List<FfmpegSource>
    {
        new($"GitHub mirror of gyan.dev {OlderVersion}", (_, _) => Task.FromResult(
            $"https://github.com/GyanD/codexffmpeg/releases/download/{OlderVersion}/ffmpeg-{OlderVersion}-essentials_build.zip"), false),
    };

    /// <summary>The folder next to Convertr.exe if it can be written to, otherwise %LOCALAPPDATA%\Convertr\ffmpeg.</summary>
    public static string ChooseWritableFolder(string preferred, string fallback)
    {
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, ".write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return preferred;
        }
        catch { /* not writable (Program Files, read-only share) */ }
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static string DefaultPreferredFolder => PlatformServices.Current.Paths.FfmpegPreferredFolder;
    public static string DefaultFallbackFolder => PlatformServices.Current.Paths.FfmpegFallbackFolder;

    /// <summary>Extracts ffmpeg, ffprobe (and a licence file when present) from the zip into the folder, replacing older copies. Returns the file names written.</summary>
    public static IReadOnlyList<string> ExtractTools(string zipPath, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        var written = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        var wanted = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var name = e.Name;
            if (name.Length == 0) continue;
            var full = e.FullName.Replace('\\', '/');
            var inBin = full.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || !full.Contains('/');
            if (inBin && (name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)))
                wanted[name] = e;
            else if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                if (full.Count(c => c == '/') <= 1) wanted.TryAdd(name, e);
        }
        if (!wanted.ContainsKey("ffmpeg.exe") || !wanted.ContainsKey("ffprobe.exe"))
            throw new DownloadException("The downloaded archive does not contain ffmpeg.exe and ffprobe.exe.");
        foreach (var (name, entry) in wanted)
        {
            var target = Path.Combine(destFolder, name);      // only our own file names are used: no path from the archive
            var tmp = target + ".part";
            entry.ExtractToFile(tmp, true);
            File.Move(tmp, target, true);
            written.Add(name);
        }
        return written;
    }

    public static async Task<DownloadResult> DownloadAsync(string destFolder, bool older, IProgress<DownloadProgress>? progress, CancellationToken ct,
        HttpMessageHandler? handler = null, IReadOnlyList<FfmpegSource>? sources = null)
    {
        sources ??= older ? OlderSources() : LatestSources();
        using var http = handler != null ? new HttpClient(handler, false) : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Convertr");
        Directory.CreateDirectory(destFolder);
        var errors = new List<string>();
        foreach (var src in sources)
        {
            var zip = Path.Combine(destFolder, "download.zip.part");
            try
            {
                progress?.Report(new DownloadProgress("Contacting " + src.Name + "...", 0, null, src.Name));
                var url = await src.ResolveUrl(http, ct).ConfigureAwait(false);
                await DownloadFileAsync(http, url, zip, src.Name, progress, ct).ConfigureAwait(false);
                if (src.ExpectedSize is long size && new FileInfo(zip).Length != size)
                    throw new DownloadException($"The download has the wrong size ({new FileInfo(zip).Length:N0} bytes, expected {size:N0}).");
                if (src.ExpectedSha256 != null)
                {
                    progress?.Report(new DownloadProgress("Checking the SHA-256...", 0, null, src.Name));
                    var actual = await Sha256Async(zip, ct).ConfigureAwait(false);
                    if (!actual.Equals(src.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new DownloadException("The download is damaged (SHA-256 does not match).");
                }
                else if (src.HasSha256)
                {
                    progress?.Report(new DownloadProgress("Checking the SHA-256...", 0, null, src.Name));
                    var expected = await TryGetSha256Async(http, url + ".sha256", ct).ConfigureAwait(false);
                    if (expected != null)
                    {
                        var actual = await Sha256Async(zip, ct).ConfigureAwait(false);
                        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                            throw new DownloadException("The download is damaged (SHA-256 does not match).");
                    }
                }
                progress?.Report(new DownloadProgress("Extracting ffmpeg.exe and ffprobe.exe...", 0, null, src.Name));
                ExtractTools(zip, destFolder);
                try { File.Delete(zip); } catch { /* ignore */ }
                return new DownloadResult(destFolder, Path.Combine(destFolder, "ffmpeg.exe"), Path.Combine(destFolder, "ffprobe.exe"), src.Name, url);
            }
            catch (OperationCanceledException) { TryDelete(zip); throw; }
            catch (Exception ex)
            {
                TryDelete(zip);
                errors.Add(src.Name + ": " + FriendlyError(ex));
            }
        }
        throw new DownloadException("Could not download FFmpeg.\n" + string.Join("\n", errors));
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ } }

    private static async Task DownloadFileAsync(HttpClient http, string url, string path, string source, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var inp = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buf = new byte[81920];
        long done = 0, lastReport = 0;
        int n;
        while ((n = await inp.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (done - lastReport >= 256 * 1024) { lastReport = done; progress?.Report(new DownloadProgress("Downloading", done, total, source)); }
        }
        progress?.Report(new DownloadProgress("Downloading", done, total ?? done, source));
        if (total != null && done != total) throw new DownloadException("The download stopped early.");
    }

    private static async Task<string?> TryGetSha256Async(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            var text = (await http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
            var m = Regex.Match(text, "^[0-9a-fA-F]{64}");
            return m.Success ? m.Value : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    /// <summary>Plain-language reason for a failed download (offline, blocked, timeout, disk).</summary>
    public static string FriendlyError(Exception ex)
    {
        switch (ex)
        {
            case DownloadException d: return d.Message;
            case HttpRequestException h when h.InnerException is System.Net.Sockets.SocketException || h.StatusCode == null:
                return "no internet connection (or the site cannot be reached)";
            case HttpRequestException h:
                return "the server answered " + (int)(h.StatusCode ?? 0) + " " + h.StatusCode;
            case TaskCanceledException: return "the connection timed out";
            case UnauthorizedAccessException: return "the folder is not writable: " + ex.Message;
            case IOException: return "could not write the file: " + ex.Message;
            case InvalidDataException: return "the downloaded file is not a valid zip";
            default: return ex.Message;
        }
    }
}
