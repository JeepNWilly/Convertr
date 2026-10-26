// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Text;

namespace Convertr.Core;

public sealed record ProbeRunResult(string Stdout, string Stderr, int ExitCode, bool TimedOut);

/// <summary>Runs ffprobe on one file. Abstracted so the scanner can be tested without ffprobe.</summary>
public interface IProbeRunner
{
    Task<ProbeRunResult> RunAsync(string path, CancellationToken ct);
}

/// <summary>
/// Runs <c>ffprobe -v error -print_format json -show_format -show_streams -show_chapters -i PATH</c>.
/// The path is passed through ArgumentList (no shell, no manual quoting), so special characters are safe.
/// ffprobe only reads the file.
/// </summary>
public sealed class FfprobeRunner : IProbeRunner
{
    private readonly string _ffprobe;
    private readonly TimeSpan _timeout;

    public FfprobeRunner(string ffprobePath, TimeSpan? timeout = null)
    {
        _ffprobe = ffprobePath;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
    }

    public async Task<ProbeRunResult> RunAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "-i", path })
            psi.ArgumentList.Add(a);
        FfmpegEnvironment.Apply(psi);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try { proc.StandardInput.Close(); } catch { /* ignore */ }
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            return new ProbeRunResult("", "ffprobe timed out", -1, true);
        }
        var stdout = await outTask.ConfigureAwait(false);
        var stderr = await errTask.ConfigureAwait(false);
        return new ProbeRunResult(stdout, stderr, proc.ExitCode, false);
    }
}
