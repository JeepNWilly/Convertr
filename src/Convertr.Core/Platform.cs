// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Convertr.Core;

// Platform seams (Convertr_svc DESIGN.md section 2.4). Everything that differs between Windows and Linux sits behind these
// interfaces. The Windows implementations are the code that used to be inline in Core (moved, not changed).

/// <summary>Where this program keeps its own files and where it looks for ffmpeg.</summary>
public interface IAppPaths
{
    /// <summary>Writable folder for state (Windows: next to the exe, or %LOCALAPPDATA%\Convertr when that is read-only; Linux: /var/lib/convertr-svc).</summary>
    string DataDir { get; }
    string LogsDir { get; }
    string SettingsPath { get; }
    string QueuePath { get; }
    string ProbeCachePath { get; }
    /// <summary>Folder searched for ffmpeg "next to the program", and where a downloaded ffmpeg goes first.</summary>
    string FfmpegPreferredFolder { get; }
    /// <summary>Where a downloaded ffmpeg goes when the preferred folder cannot be written.</summary>
    string FfmpegFallbackFolder { get; }
    /// <summary>Per-user data folder searched for a downloaded ffmpeg (Windows: %LOCALAPPDATA%); empty when there is none.</summary>
    string UserDataFolder { get; }
}

/// <summary>Pause and resume a running process (Windows: NtSuspendProcess; Linux: SIGSTOP / SIGCONT).</summary>
public interface IProcessController
{
    bool CanSuspend { get; }
    void Suspend(Process process);
    void Resume(Process process);
}

/// <summary>File time stamps. Creation time can be set on Windows only; Linux keeps the modified and access times.</summary>
public interface IFileTimes
{
    bool CanSetCreationTime { get; }
    DateTime GetCreationUtc(string path);
    void SetCreationUtc(string path, DateTime utc);
}

/// <summary>Finds the computer's CPU and GPUs (Windows: CIM + nvidia-smi; Linux: /proc/cpuinfo + lspci + nvidia-smi).</summary>
public interface IPlatformHardware
{
    Task<CpuInfo> DetectCpuAsync(IHardwareProbe probe, CancellationToken ct);
    Task<List<GpuInfo>> DetectGpusAsync(IHardwareProbe probe, CancellationToken ct);
}

/// <summary>Everything platform specific, chosen once at start-up.</summary>
public interface IPlatformServices
{
    string Name { get; }
    /// <summary>True when "A.mkv" and "a.mkv" are different files (Linux).</summary>
    bool PathsCaseSensitive { get; }
    char PathListSeparator { get; }
    string ExeName(string baseName);
    /// <summary>Extra folders searched for ffmpeg before PATH (Linux: jellyfin-ffmpeg and /usr/local/bin). Empty on Windows.</summary>
    IReadOnlyList<string> ExtraToolDirectories { get; }
    string MachineName { get; }
    IAppPaths Paths { get; }
    IProcessController Process { get; }
    IFileTimes FileTimes { get; }
    IPlatformHardware Hardware { get; }
}

/// <summary>The platform in use. Tests may replace <see cref="Current"/>.</summary>
public static class PlatformServices
{
    private static IPlatformServices? _current;
    public static IPlatformServices Current
    {
        get => _current ??= Create();
        set => _current = value;
    }

    public static IPlatformServices Create() => OperatingSystem.IsWindows() ? new WindowsPlatform() : new LinuxPlatform();
}

/// <summary>Path equality and ordering that follow the platform: case-insensitive on Windows, case-sensitive on Linux.</summary>
public static class PathCompare
{
    public static StringComparison Comparison => PlatformServices.Current.PathsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    public static StringComparer Comparer => PlatformServices.Current.PathsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    public static bool Equal(string? a, string? b) => string.Equals(a, b, Comparison);
    public static bool StartsWith(string value, string prefix) => value.StartsWith(prefix, Comparison);
}

/// <summary>
/// Applies extra environment variables to every ffmpeg / ffprobe process (Convertr_svc DESIGN.md section 5A.2).
/// On Battlemage-class Intel GPUs jellyfin-ffmpeg must use the system libva / iHD instead of its bundled copy.
/// Nothing is applied by default (Windows behaviour unchanged).
/// </summary>
public interface IFfmpegEnvironment
{
    void Apply(ProcessStartInfo psi);
}

public sealed class NoFfmpegEnvironment : IFfmpegEnvironment
{
    public static readonly NoFfmpegEnvironment Instance = new();
    public void Apply(ProcessStartInfo psi) { }
}

/// <summary>Applies NAME=VALUE pairs (from the FfmpegEnvironment setting) to the process environment.</summary>
public sealed class ListFfmpegEnvironment : IFfmpegEnvironment
{
    private readonly List<(string Name, string Value)> _pairs = new();

    public ListFfmpegEnvironment(IEnumerable<string> pairs)
    {
        foreach (var p in pairs ?? Array.Empty<string>())
        {
            var i = p.IndexOf('=');
            if (i > 0) _pairs.Add((p[..i].Trim(), p[(i + 1)..]));
        }
    }

    public IReadOnlyList<(string Name, string Value)> Pairs => _pairs;

    public void Apply(ProcessStartInfo psi)
    {
        foreach (var (n, v) in _pairs) if (n.Length > 0) psi.Environment[n] = v;
    }
}

public static class FfmpegEnvironment
{
    /// <summary>The three variables that make jellyfin-ffmpeg use the system libva / iHD driver (measured fix on the Arc Pro B60).</summary>
    public static readonly string[] SystemLibsPairs =
    {
        "LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu",
        "LIBVA_DRIVERS_PATH=/usr/lib/x86_64-linux-gnu/dri",
        "LIBVA_DRIVER_NAME=iHD",
    };

    public static IFfmpegEnvironment Current { get; set; } = NoFfmpegEnvironment.Instance;

    public static void Apply(ProcessStartInfo psi) => Current.Apply(psi);

    /// <summary>Environment for the given setting: the list when it has entries, otherwise nothing.</summary>
    public static IFfmpegEnvironment For(Settings s) =>
        s.FfmpegEnvironment.Count > 0 ? new ListFfmpegEnvironment(s.FfmpegEnvironment) : NoFfmpegEnvironment.Instance;
}

/// <summary>Windows: the code that used to be inline in Core.</summary>
public sealed class WindowsPlatform : IPlatformServices
{
    public string Name => "Windows";
    public bool PathsCaseSensitive => false;
    public char PathListSeparator => ';';
    public string ExeName(string baseName) => baseName + ".exe";
    public IReadOnlyList<string> ExtraToolDirectories => Array.Empty<string>();
    public string MachineName => Environment.MachineName;
    public IAppPaths Paths { get; } = new WindowsAppPaths();
    public IProcessController Process { get; } = new WindowsProcessController();
    public IFileTimes FileTimes { get; } = new WindowsFileTimes();
    public IPlatformHardware Hardware { get; } = new WindowsHardware();
}

public sealed class WindowsAppPaths : IAppPaths
{
    private string? _dir;
    public string DataDir => _dir ??= Resolve();
    public string LogsDir => Path.Combine(DataDir, "Logs");
    public string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    public string QueuePath => Path.Combine(DataDir, "queue.json");
    public string ProbeCachePath => Path.Combine(AppContext.BaseDirectory, "probe_cache.json");
    public string FfmpegPreferredFolder => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    public string FfmpegFallbackFolder => Path.Combine(UserDataFolder, "ffmpeg");
    public string UserDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convertr");

    /// <summary>Next to the exe, or %LOCALAPPDATA%\Convertr when that folder cannot be written.</summary>
    private string Resolve()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, ".write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(p, "x");
            File.Delete(p);
            return AppContext.BaseDirectory;
        }
        catch
        {
            var d = UserDataFolder;
            Directory.CreateDirectory(d);
            return d;
        }
    }
}

public sealed class WindowsProcessController : IProcessController
{
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr handle);

    public bool CanSuspend => OperatingSystem.IsWindows();
    public void Suspend(Process process) { if (OperatingSystem.IsWindows()) NtSuspendProcess(process.Handle); }
    public void Resume(Process process) { if (OperatingSystem.IsWindows()) NtResumeProcess(process.Handle); }
}

public sealed class WindowsFileTimes : IFileTimes
{
    public bool CanSetCreationTime => true;
    public DateTime GetCreationUtc(string path) => File.GetCreationTimeUtc(path);
    public void SetCreationUtc(string path, DateTime utc) => File.SetCreationTimeUtc(path, utc);
}

public sealed class WindowsHardware : IPlatformHardware
{
    private static string[] Ps(string command) => new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command };
    private static readonly string[] SmiArgs = { "--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits" };

    public async Task<CpuInfo> DetectCpuAsync(IHardwareProbe probe, CancellationToken ct) =>
        HardwareParsers.ParseCpu(await probe.RunAsync("powershell.exe", Ps(HardwareDetector.CpuQuery), ct).ConfigureAwait(false));

    public async Task<List<GpuInfo>> DetectGpusAsync(IHardwareProbe probe, CancellationToken ct)
    {
        var cimTask = probe.RunAsync("powershell.exe", Ps(HardwareDetector.GpuQuery), ct);
        var smiTask = probe.RunAsync("nvidia-smi", SmiArgs, ct);
        return HardwareParsers.Merge(HardwareParsers.ParseGpus(await cimTask.ConfigureAwait(false)), HardwareParsers.ParseNvidiaSmi(await smiTask.ConfigureAwait(false)));
    }
}
