// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Convertr.Core;

/// <summary>What the Intel GPU encoder tests found (Convertr_svc). Saved in the hardware profile.</summary>
public sealed class IntelCapabilities
{
    public bool Tested { get; set; }
    public string Device { get; set; } = "";
    public string Driver { get; set; } = "";
    public bool VaapiHevc { get; set; }
    public bool QsvHevc { get; set; }
    public bool VaapiAv1 { get; set; }
    public bool QsvAv1 { get; set; }
    /// <summary>Rate-control options that really worked (with {Q}); empty when that encoder does not work.</summary>
    public string VaapiHevcArgs { get; set; } = "";
    public string QsvHevcArgs { get; set; } = "";
    public string VaapiAv1Args { get; set; } = "";
    public string QsvAv1Args { get; set; } = "";
    /// <summary>The bundled libraries crashed (Bus error / exit 135) and the system libva / iHD environment fixed it.</summary>
    public bool SystemLibsNeeded { get; set; }
    /// <summary>NAME=VALUE pairs that were in effect for the successful tests.</summary>
    public List<string> Environment { get; set; } = new();
    /// <summary>Empty when HEVC works; otherwise a readable reason with a suggestion.</summary>
    public string Reason { get; set; } = "";
    public List<string> Log { get; set; } = new();
    [JsonIgnore] public bool Any => VaapiHevc || QsvHevc;
    public string Summary =>
        !Tested ? "not tested" :
        !Any ? "no working Intel hardware encoder" :
        "works: " + string.Join(", ", new[] { VaapiHevc ? "VAAPI HEVC" : null, QsvHevc ? "QSV HEVC" : null, VaapiAv1 ? "VAAPI AV1" : null, QsvAv1 ? "QSV AV1" : null }.Where(x => x != null));
}

/// <summary>Starts ffmpeg-like processes for the Intel tests (replaceable in tests).</summary>
public interface IIntelProcessRunner
{
    Task<(int ExitCode, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyList<string> envPairs, TimeSpan timeout, CancellationToken ct);
}

public sealed class IntelProcessRunner : IIntelProcessRunner
{
    public async Task<(int ExitCode, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyList<string> envPairs, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var p in envPairs) { var i = p.IndexOf('='); if (i > 0) psi.Environment[p[..i].Trim()] = p[(i + 1)..]; }
        try
        {
            using var proc = Process.Start(psi)!;
            try { proc.StandardInput.Close(); } catch { /* ignore */ }
            var err = proc.StandardError.ReadToEndAsync(ct);
            _ = proc.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { try { proc.Kill(true); } catch { /* gone */ } return (-1, "timed out"); }
            return (proc.ExitCode, (await err.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (-2, ex.Message); }
    }
}

/// <summary>
/// Finds out what the Intel GPU can really do with this ffmpeg: whether the bundled libraries crash (then the system libva / iHD
/// environment is used), which rate-control options VAAPI and QSV accept, and whether AV1 works. Everything is decided by test encodes.
/// </summary>
public static class IntelProbe
{
    public static bool IsCrash(int exitCode, string stderr) =>
        exitCode is 135 or 139 or 134 or 132 or 136 or -7 or -11 or -6
        || stderr.Contains("Bus error", StringComparison.OrdinalIgnoreCase) || stderr.Contains("Segmentation fault", StringComparison.OrdinalIgnoreCase);

    public static async Task<IntelCapabilities> DetectAsync(FfmpegLocation? loc, Settings s, IIntelProcessRunner? runner = null, string? deviceOverride = null, CancellationToken ct = default)
    {
        runner ??= new IntelProcessRunner();
        var caps = new IntelCapabilities { Tested = true };
        if (loc == null || string.IsNullOrEmpty(loc.FfmpegPath)) { caps.Reason = "ffmpeg was not found"; return caps; }
        var dev = deviceOverride ?? (!string.IsNullOrWhiteSpace(s.GpuDevice) ? s.GpuDevice : IntelHw.FirstRenderNode());
        if (string.IsNullOrEmpty(dev)) { caps.Reason = "no /dev/dri render node found: is the Intel GPU driver (xe / i915) loaded and the service user in the 'render' group?"; return caps; }
        caps.Device = dev;
        var ffmpeg = loc.FfmpegPath;
        var timeout = TimeSpan.FromSeconds(40);

        async Task<(bool Ok, int Code, string Err)> Try(GpuBackend b, bool av1, string template, IReadOnlyList<string> env)
        {
            var probeSettings = new Settings { GpuDevice = dev, GpuBackend = b };
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
            args.AddRange(IntelHw.InitArgs(probeSettings));
            args.AddRange(new[] { "-f", "lavfi", "-i", "testsrc=s=1280x720:r=25:d=1", "-vf", "format=nv12," + (b == GpuBackend.Qsv ? "hwupload=extra_hw_frames=64" : "hwupload"),
                                  "-c:v", IntelHw.EncoderName(b, av1) });
            args.AddRange(template.Replace("{Q}", av1 ? "27" : "23").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            args.AddRange(new[] { "-frames:v", "25", "-f", "null", "-" });
            var (code, err) = await runner.RunAsync(ffmpeg, args, env, timeout, ct).ConfigureAwait(false);
            caps.Log.Add($"{IntelHw.EncoderName(b, av1)} [{template}] env={(env.Count == 0 ? "bundled" : "system")}: exit {code}" + (code == 0 ? "" : " " + FirstLine(err)));
            return (code == 0, code, err);
        }

        // 1. environment: configured list, or bundled libraries first and the system libs when the bundled ones crash
        List<string> envUsed;
        if (s.FfmpegEnvironment.Count > 0 && !s.FfmpegEnvironmentAuto) envUsed = s.FfmpegEnvironment.ToList();
        else
        {
            envUsed = new();
            var first = await Try(GpuBackend.Vaapi, false, IntelHw.Candidates(GpuBackend.Vaapi, false)[0], envUsed).ConfigureAwait(false);
            if (!first.Ok && IsCrash(first.Code, first.Err))
            {
                var sys = FfmpegEnvironment.SystemLibsPairs.ToList();
                var second = await Try(GpuBackend.Vaapi, false, IntelHw.Candidates(GpuBackend.Vaapi, false)[0], sys).ConfigureAwait(false);
                if (second.Ok || !IsCrash(second.Code, second.Err)) { envUsed = sys; caps.SystemLibsNeeded = true; }
            }
        }
        caps.Environment = envUsed;

        // 2. which options work, per backend / codec
        async Task<string> Find(GpuBackend b, bool av1)
        {
            foreach (var cand in IntelHw.Candidates(b, av1))
            {
                ct.ThrowIfCancellationRequested();
                var r = await Try(b, av1, cand, envUsed).ConfigureAwait(false);
                if (r.Ok) return cand;
                if (r.Err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) { caps.Reason = $"permission denied on {dev}: add the service user to the 'render' group (sudo usermod -aG render <user>) and restart the service"; return ""; }
                if (r.Err.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) || r.Err.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase) && cand == IntelHw.Candidates(b, av1)[^1]) break;
            }
            return "";
        }
        caps.VaapiHevcArgs = await Find(GpuBackend.Vaapi, false).ConfigureAwait(false);
        caps.QsvHevcArgs = await Find(GpuBackend.Qsv, false).ConfigureAwait(false);
        caps.VaapiHevc = caps.VaapiHevcArgs.Length > 0;
        caps.QsvHevc = caps.QsvHevcArgs.Length > 0;
        caps.VaapiAv1Args = await Find(GpuBackend.Vaapi, true).ConfigureAwait(false);
        caps.QsvAv1Args = await Find(GpuBackend.Qsv, true).ConfigureAwait(false);
        caps.VaapiAv1 = caps.VaapiAv1Args.Length > 0;
        caps.QsvAv1 = caps.QsvAv1Args.Length > 0;

        // 3. driver line (info only)
        try
        {
            var (vc, vout) = await runner.RunAsync("vainfo", new[] { "--display", "drm", "--device", dev }, envUsed, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            _ = vc;
            caps.Driver = vout.Split('\n').FirstOrDefault(l => l.Contains("Driver version", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch { /* vainfo is optional */ }

        if (!caps.Any && caps.Reason.Length == 0)
            caps.Reason = "no Intel hardware HEVC encoder worked in the test encodes (see the log); check the driver (xe / i915, intel-media-va-driver-non-free) and the 'render' group";
        return caps;
    }

    /// <summary>Applies a successful test to the settings (backend, rate-control options, environment). Only fills what the user left on Auto.</summary>
    public static void Apply(Settings s, IntelCapabilities caps)
    {
        if (!caps.Any) return;
        if (s.FfmpegEnvironment.Count == 0 || s.FfmpegEnvironmentAuto) s.FfmpegEnvironment = caps.Environment.ToList();
        if (s.GpuBackend is GpuBackend.Auto or GpuBackend.Nvenc) s.GpuBackend = caps.VaapiHevc ? GpuBackend.Vaapi : GpuBackend.Qsv;
        var av1 = s.GpuCodec == GpuCodec.Av1;
        s.GpuEncoderArgs = s.GpuBackend == GpuBackend.Qsv ? (av1 ? caps.QsvAv1Args : caps.QsvHevcArgs) : (av1 ? caps.VaapiAv1Args : caps.VaapiHevcArgs);
        if (string.IsNullOrEmpty(s.GpuEncoderArgs)) s.GpuEncoderArgs = "";
        if (string.IsNullOrWhiteSpace(s.GpuDevice) && caps.Device.Length > 0) s.GpuDevice = caps.Device;
        FfmpegEnvironment.Current = FfmpegEnvironment.For(s);
    }

    private static string FirstLine(string t) => (t ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
