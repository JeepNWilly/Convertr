// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertr.Core;

/// <summary>One downloadable release (essentials build zip from the official gyan.dev release builds, hosted on GitHub).</summary>
public sealed record FfmpegVersion(string Version, string Url, long? SizeBytes, DateTime? Published, string? Sha256);

public enum DriverFit { Unknown, Works, NeedsNewerDriver, NoNvidia }

public sealed record VersionNote(string Text, DriverFit Fit, string? RequiredDriver);

public sealed record VersionLookup(IReadOnlyList<FfmpegVersion> Versions, bool Online, string? Error);

/// <summary>
/// The list behind "Download older" (Setup): the releases of GyanD/codexffmpeg (the GitHub home of the gyan.dev release builds) read online,
/// with a built-in list for when the lookup fails. Also the short notes (which NVIDIA driver the version needs for NVENC).
/// </summary>
public static class FfmpegVersions
{
    public const string ReleasesApi = "https://api.github.com/repos/GyanD/codexffmpeg/releases?per_page=100&page=";
    public const int MaxPages = 8;

    private static string UrlFor(string v) => $"https://github.com/GyanD/codexffmpeg/releases/download/{v}/ffmpeg-{v}-essentials_build.zip";

    /// <summary>Used when the online list cannot be read. Every URL was checked (HTTP 200, size as listed).</summary>
    public static IReadOnlyList<FfmpegVersion> Fallback { get; } = new List<FfmpegVersion>
    {
        new("8.0.1", UrlFor("8.0.1"), 106259850, new DateTime(2025, 11, 20), "e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673"),
        new("8.0", UrlFor("8.0"), 105748282, new DateTime(2025, 8, 22), "647e467caf82b9fa200a562769b5ff4d736aaf725804ed2c64ea9752106fa569"),
        new("7.1.1", UrlFor("7.1.1"), 92234348, new DateTime(2025, 3, 12), null),
        new("7.0.2", UrlFor("7.0.2"), 91406388, new DateTime(2024, 8, 3), null),
        new("6.1.1", UrlFor("6.1.1"), 87520045, new DateTime(2023, 12, 31), null),
    };

    private static readonly Regex ReleaseTag = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>Reads one page of the GitHub releases JSON: only numbered releases (not the dated git builds) that have an essentials_build zip.</summary>
    public static List<FfmpegVersion> ParseReleases(string json)
    {
        var list = new List<FfmpegVersion>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!ReleaseTag.IsMatch(tag)) continue;
            if (r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Equals($"ffmpeg-{tag}-essentials_build.zip", StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = UrlFor(tag);
                long? size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : null;
                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    var m = Regex.Match(dg.GetString() ?? "", "^sha256:([0-9a-fA-F]{64})$");
                    if (m.Success) sha = m.Groups[1].Value.ToLowerInvariant();
                }
                DateTime? when = r.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String &&
                                 DateTime.TryParse(pa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt.Date : null;
                list.Add(new FfmpegVersion(tag, url!, size, when, sha));
                break;
            }
        }
        return list;
    }

    public static int Compare(string a, string b)
    {
        static Version P(string s) => Version.TryParse(s, out var v) ? v : new Version(0, 0);
        return P(a).CompareTo(P(b));
    }

    /// <summary>Newest first, one entry per version, without the newest release (that is what the normal Download button gets).</summary>
    public static List<FfmpegVersion> OlderThanLatest(IEnumerable<FfmpegVersion> all)
    {
        var sorted = all.GroupBy(v => v.Version).Select(g => g.First()).OrderByDescending(v => v.Version, Comparer<string>.Create(Compare)).ToList();
        if (sorted.Count > 0) sorted.RemoveAt(0);
        return sorted;
    }

    /// <summary>Reads the releases online (several pages: the dated git builds are in between). Never throws; on failure the built-in list is returned.</summary>
    public static async Task<VersionLookup> LookupAsync(CancellationToken ct, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = handler != null ? new HttpClient(handler, false) : new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Convertr");
            var all = new List<FfmpegVersion>();
            for (var page = 1; page <= MaxPages; page++)
            {
                using var resp = await http.GetAsync(ReleasesApi + page, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                var count = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
                all.AddRange(ParseReleases(text));
                if (count < 100) break;
            }
            var older = OlderThanLatest(all);
            if (older.Count == 0) return new VersionLookup(Fallback, false, "the release list had no numbered versions");
            return new VersionLookup(older, true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new VersionLookup(Fallback, false, FfmpegDownloader.FriendlyError(ex)); }
    }

    private static (int Major, int Minor)? Mm(string version) =>
        Version.TryParse(version, out var v) ? (v.Major, v.Minor) : null;

    /// <summary>NVENC API version this ffmpeg release was built for, and the lowest NVIDIA driver that has it.</summary>
    private static (string Api, string Driver)? Nvenc(string version)
    {
        if (Mm(version) is not var (maj, min)) return null;
        if (maj > 8 || (maj == 8 && min >= 1)) return ("13.1", "610.00");
        if (maj == 8) return ("13.0", "570.00");
        if (maj == 7) return ("12.2", "551.76");
        if (maj == 6 && min >= 1) return ("12.1", "530.41");
        if (maj == 6) return ("12.0", "520.56");
        if (maj == 5) return ("11.1", "471.41");
        if (maj == 4) return ("11.0", "456.71");
        return null;
    }

    /// <summary>Lowest NVIDIA driver for NVENC with this ffmpeg release (null = unknown release).</summary>
    public static string? RequiredDriver(string version) => Nvenc(version)?.Driver;

    public static bool TryParseDriver(string? driver, out Version v) => Version.TryParse((driver ?? "").Trim(), out v!);

    /// <summary>Short note for the list, and whether it is known to work with the detected NVIDIA driver (null profile = unknown).</summary>
    public static VersionNote Describe(string version, HardwareProfile? hw)
    {
        var n = Nvenc(version);
        var req = n?.Driver;
        var mm = Mm(version);
        string text;
        if (n == null) text = "Older release.";
        else if (mm is { Major: > 8 } or { Major: 8, Minor: >= 1 })
            text = $"Needs NVIDIA driver {Short(n.Value.Driver)} or newer for NVENC (API {n.Value.Api}); GeForce 10 series cards cannot get that. x265 on the CPU works.";
        else if (mm is { Major: 8 })
            text = $"Works with older NVIDIA drivers (NVENC API {n.Value.Api}, driver {Short(n.Value.Driver)} or newer), including GeForce 10 series. Use it when 9.x does not find NVENC.";
        else
            text = $"Works with older NVIDIA drivers (NVENC API {n.Value.Api}, driver {Short(n.Value.Driver)} or newer). An older x265.";
        if (hw == null) return new VersionNote(text, DriverFit.Unknown, req);
        var nv = hw.Gpus.FirstOrDefault(g => g.Vendor == "NVIDIA");
        if (nv == null) return new VersionNote(text + " No NVIDIA GPU detected here, so only CPU encoding applies.", DriverFit.NoNvidia, req);
        if (req == null || !TryParseDriver(nv.Driver, out var have) || !TryParseDriver(req, out var need) || have.Major < 100)   // NVIDIA's own number looks like 582.78; anything else is not comparable
            return new VersionNote(text, DriverFit.Unknown, req);
        return have >= need
            ? new VersionNote(text + $" Known to work with your {nv.Name} driver {nv.Driver}.", DriverFit.Works, req)
            : new VersionNote(text + $" Your driver {nv.Driver} is older than {Short(req)}: NVENC will not work with this version.", DriverFit.NeedsNewerDriver, req);
    }

    private static string Short(string driver) => driver.EndsWith(".00", StringComparison.Ordinal) ? driver[..^3] : driver;

    /// <summary>Where a version goes: a folder of its own under the ffmpeg folder (ffmpeg\8.0.1), so versions never overwrite each other.</summary>
    public static string FolderFor(string root, string version)
    {
        var safe = Regex.Replace(version, @"[^0-9A-Za-z._-]", "_");
        if (safe.Length == 0 || safe.All(c => c == '.')) safe = "_";   // never "." or ".."
        return Path.Combine(root, safe);
    }

    /// <summary>True when ffmpeg.exe and ffprobe.exe are already in the version's folder (nothing to download).</summary>
    public static bool IsInstalled(string folder) => File.Exists(Path.Combine(folder, "ffmpeg.exe")) && File.Exists(Path.Combine(folder, "ffprobe.exe"));

    public static FfmpegSource ToSource(FfmpegVersion v) =>
        new($"GitHub release {v.Version} (gyan.dev build)", (_, _) => Task.FromResult(v.Url), false, v.Sha256, v.SizeBytes);
}
