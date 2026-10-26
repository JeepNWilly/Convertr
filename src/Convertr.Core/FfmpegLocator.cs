// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Convertr.Core;

public sealed record FfmpegLocation(string Folder, string FfmpegPath, string FfprobePath, string Source);

public sealed record FfmpegCapabilities(string Version, bool Nvenc264, bool NvencHevc, bool X264, bool X265);

/// <summary>Finds ffmpeg and ffprobe and reports the version and encoders.</summary>
public static class FfmpegLocator
{
    private static string Exe(string name) => PlatformServices.Current.ExeName(name);

    private static FfmpegLocation? InFolder(string? folder, string source)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var probe = Path.Combine(folder.Trim(), Exe("ffprobe"));
            var mpeg = Path.Combine(folder.Trim(), Exe("ffmpeg"));
            if (File.Exists(probe)) return new FfmpegLocation(folder.Trim(), File.Exists(mpeg) ? mpeg : "", probe, source);
        }
        catch { /* bad path characters */ }
        return null;
    }

    /// <summary>
    /// Order: the folder saved in Setup (if set and valid), the folder next to the exe (and its ffmpeg subfolder), %LOCALAPPDATA%\\Convertr\\ffmpeg (where Download FFmpeg puts it when the exe folder is read-only), then PATH. There is no built-in folder: nothing is assumed about this computer.
    /// </summary>
    public static FfmpegLocation? Find(string? configuredFolder, string? exeFolder = null, string? pathEnv = null, string? localAppDataFolder = null)
    {
        var hit = InFolder(configuredFolder, "Setup");
        if (hit != null) return hit;
        var exe = exeFolder ?? AppContext.BaseDirectory;
        hit = InFolder(exe, "next to Convertr") ?? InFolder(Path.Combine(exe, "ffmpeg"), "ffmpeg folder next to Convertr");
        if (hit != null) return hit;
        var plat = PlatformServices.Current;
        if (exeFolder == null) { hit = InFolder(plat.Paths.FfmpegPreferredFolder, "ffmpeg folder of this program"); if (hit != null) return hit; }
        var userData = localAppDataFolder != null ? Path.Combine(localAppDataFolder, "Convertr") : plat.Paths.UserDataFolder;
        if (!string.IsNullOrEmpty(userData) && !string.IsNullOrEmpty(localAppDataFolder ?? userData))
        {
            hit = InFolder(Path.Combine(userData, "ffmpeg"), "downloaded ffmpeg (local app data)");
            if (hit != null) return hit;
        }
        if (pathEnv == null)
            foreach (var dir in plat.ExtraToolDirectories)
            {
                hit = InFolder(dir, "installed ffmpeg");
                if (hit != null) return hit;
            }
        var sep = plat.PathListSeparator;
        foreach (var dir in (pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            hit = InFolder(dir.Trim('"'), "PATH");
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Runs ffmpeg -version and -encoders (read-only).</summary>
    public static async Task<FfmpegCapabilities?> DetectAsync(FfmpegLocation loc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(loc.FfmpegPath)) return null;
        var ver = await RunAsync(loc.FfmpegPath, new[] { "-hide_banner", "-version" }, ct).ConfigureAwait(false);
        var enc = await RunAsync(loc.FfmpegPath, new[] { "-hide_banner", "-encoders" }, ct).ConfigureAwait(false);
        var m = Regex.Match(ver, @"version\s+(\S+)");
        return new FfmpegCapabilities(
            m.Success ? m.Groups[1].Value : "unknown",
            Regex.IsMatch(enc, @"\bh264_nvenc\b"), Regex.IsMatch(enc, @"\bhevc_nvenc\b"),
            Regex.IsMatch(enc, @"\blibx264\b"), Regex.IsMatch(enc, @"\blibx265\b"));
    }

    /// <summary>
    /// Really starts the NVENC encoder on a tiny generated picture (nothing is read from or written to any library).
    /// "Encoder listed" is not enough: the driver must also support the NVENC API version this ffmpeg was built for.
    /// </summary>
    public static Task<(bool Ok, string Message)> TestNvencAsync(FfmpegLocation loc, bool hevc = true, CancellationToken ct = default) =>
        TestEncoderAsync(loc, hevc ? "hevc_nvenc" : "h264_nvenc", ct);

    /// <summary>Starts any encoder (hevc_nvenc, h264_nvenc, hevc_qsv, hevc_amf...) on a 1 s generated test picture (lavfi testsrc), output to nowhere.</summary>
    public static async Task<(bool Ok, string Message)> TestEncoderAsync(FfmpegLocation loc, string encoder, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(loc.FfmpegPath)) return (false, "ffmpeg.exe is missing");
        var psi = new ProcessStartInfo
        {
            FileName = loc.FfmpegPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=1", "-c:v", encoder, "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        FfmpegEnvironment.Apply(psi);
        try
        {
            using var p = Process.Start(psi)!;
            try { p.StandardInput.Close(); } catch { /* ignore */ }
            var err = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* gone */ } return (false, "the NVENC test timed out"); }
            var text = (await err.ConfigureAwait(false)).Trim();
            return p.ExitCode == 0 ? (true, "") : (false, NvencProblem(text));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>A short, readable reason for a failed NVENC test.</summary>
    public static string NvencProblem(string stderr)
    {
        var api = Regex.Match(stderr, @"Required:\s*([0-9.]+)\s*Found:\s*([0-9.]+)");
        if (api.Success)
        {
            // ffmpeg 8.1 and newer print "The minimum required Nvidia driver for nvenc is 610.00 or newer". GeForce 10-series (Pascal) cards
            // stopped at the 580 driver branch, so for them updating can never help.
            var min = Regex.Match(stderr, @"minimum required Nvidia driver for nvenc is\s*([0-9.]+)", RegexOptions.IgnoreCase);
            var need = min.Success ? $" (driver {min.Groups[1].Value.TrimEnd('0').TrimEnd('.')} or newer)" : "";
            return $"this ffmpeg needs NVENC API {api.Groups[1].Value}{need} but your NVIDIA driver provides {api.Groups[2].Value}. Update the driver (older cards such as the GTX 10 series have no newer driver), or use an older ffmpeg build (8.0.1 works), or use CPU mode.";
        }
        if (Regex.IsMatch(stderr, "No NVENC capable devices found|Cannot load nvcuda|OpenEncodeSessionEx failed|CUDA_ERROR_NO_DEVICE", RegexOptions.IgnoreCase))
            return "no NVENC-capable NVIDIA GPU or driver was found. Use CPU mode.";
        var first = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "the NVENC test failed" : first;
    }

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        FfmpegEnvironment.Apply(psi);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return await o.ConfigureAwait(false);
    }
}
