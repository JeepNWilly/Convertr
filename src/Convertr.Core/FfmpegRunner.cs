// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Text;

namespace Convertr.Core;

public sealed record FfmpegRunResult(int ExitCode, string StderrTail, bool Cancelled);

/// <summary>Lets the engine suspend and resume the running ffmpeg process (Pause / Resume).</summary>
public sealed class ProcessControl
{
    private Action? _suspend, _resume;
    public bool Suspended { get; private set; }

    public void Attach(Action suspend, Action resume) { _suspend = suspend; _resume = resume; }
    public void Detach() { _suspend = null; _resume = null; Suspended = false; }

    public bool Suspend()
    {
        if (_suspend == null) return false;
        _suspend(); Suspended = true; return true;
    }

    public void Resume()
    {
        if (!Suspended) return;
        _resume?.Invoke(); Suspended = false;
    }
}

/// <summary>Runs ffmpeg. Abstracted so the engine can be tested without ffmpeg.</summary>
public interface IFfmpegRunner
{
    Task<FfmpegRunResult> RunAsync(IReadOnlyList<string> args, IProgress<ProgressSnapshot>? progress, ProcessControl? control, CancellationToken ct);
}

/// <summary>Starts the real ffmpeg: arguments go through ArgumentList (no shell), progress comes from "-progress pipe:1", cancel kills the process tree.</summary>
public sealed class FfmpegProcessRunner : IFfmpegRunner
{
    private readonly string _ffmpeg;
    private readonly ProcessPriorityClass _priority;

    public FfmpegProcessRunner(string ffmpegPath, string priority = "BelowNormal")
    {
        _ffmpeg = ffmpegPath;
        _priority = priority.ToLowerInvariant() switch
        {
            "normal" => ProcessPriorityClass.Normal,
            "idle" => ProcessPriorityClass.Idle,
            _ => ProcessPriorityClass.BelowNormal,
        };
    }

    public async Task<FfmpegRunResult> RunAsync(IReadOnlyList<string> args, IProgress<ProgressSnapshot>? progress, ProcessControl? control, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        FfmpegEnvironment.Apply(psi);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tail = new StringBuilder();
        var parser = new ProgressParser();
        proc.OutputDataReceived += (_, e) =>
        {
            var snap = parser.Feed(e.Data);
            if (snap != null) progress?.Report(snap);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (tail) { tail.AppendLine(e.Data); if (tail.Length > 8000) tail.Remove(0, tail.Length - 6000); }
        };
        proc.Start();
        try { proc.PriorityClass = _priority; } catch { /* not allowed: keep default */ }
        try { proc.StandardInput.Close(); } catch { /* ignore */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var pc = PlatformServices.Current.Process;
        if (control != null && pc.CanSuspend)
            control.Attach(() => pc.Suspend(proc), () => pc.Resume(proc));

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (control?.Suspended == true) control.Resume(); } catch { /* ignore */ }
            try { proc.Kill(true); } catch { /* already gone */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            control?.Detach();
            return new FfmpegRunResult(-1, "", true);
        }
        proc.WaitForExit();   // flush the redirected streams
        control?.Detach();
        string t; lock (tail) t = tail.ToString().Trim();
        return new FfmpegRunResult(proc.ExitCode, t, false);
    }
}
