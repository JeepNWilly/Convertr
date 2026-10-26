// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Convertr.Core;

/// <summary>Linux (Convertr_svc): state under /var/lib/convertr-svc (override with CONVERTR_DATA_DIR), SIGSTOP/SIGCONT, no creation-time setting.</summary>
public sealed class LinuxPlatform : IPlatformServices
{
    public string Name => "Linux";
    public bool PathsCaseSensitive => true;
    public char PathListSeparator => ':';
    public string ExeName(string baseName) => baseName;
    public IReadOnlyList<string> ExtraToolDirectories { get; } = new[] { "/usr/lib/jellyfin-ffmpeg", "/usr/local/bin", "/usr/bin" };
    public string MachineName => Environment.MachineName;
    public IAppPaths Paths { get; } = new LinuxAppPaths();
    public IProcessController Process { get; } = new LinuxProcessController();
    public IFileTimes FileTimes { get; } = new LinuxFileTimes();
    public IPlatformHardware Hardware { get; } = new LinuxHardware();
}

public sealed class LinuxAppPaths : IAppPaths
{
    private readonly string _dir;
    public LinuxAppPaths(string? dataDir = null) =>
        _dir = !string.IsNullOrWhiteSpace(dataDir) ? dataDir : Environment.GetEnvironmentVariable("CONVERTR_DATA_DIR") is { Length: > 0 } e ? e : "/var/lib/convertr-svc";
    public string DataDir => _dir;
    public string LogsDir => Path.Combine(_dir, "logs");
    public string SettingsPath => Path.Combine(_dir, "settings.json");
    public string QueuePath => Path.Combine(_dir, "queue.json");
    public string ProbeCachePath => Path.Combine(_dir, "probe-cache.json");
    public string FfmpegPreferredFolder => Path.Combine(_dir, "ffmpeg");
    public string FfmpegFallbackFolder => Path.Combine(_dir, "ffmpeg");
    public string UserDataFolder => "";
}

public sealed class LinuxProcessController : IProcessController
{
    private const int SigStop = 19, SigCont = 18;
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);

    public bool CanSuspend => OperatingSystem.IsLinux();
    public void Suspend(Process process) { if (OperatingSystem.IsLinux()) kill(process.Id, SigStop); }
    public void Resume(Process process) { if (OperatingSystem.IsLinux()) kill(process.Id, SigCont); }
}

/// <summary>The birth time of a file cannot be set on Linux; the modified and access times are what matter (they are still copied).</summary>
public sealed class LinuxFileTimes : IFileTimes
{
    public bool CanSetCreationTime => false;
    public DateTime GetCreationUtc(string path) => File.GetCreationTimeUtc(path);
    public void SetCreationUtc(string path, DateTime utc) { /* not possible on Linux */ }
}

public sealed class LinuxHardware : IPlatformHardware
{
    private static readonly string[] SmiArgs = { "--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits" };
    private readonly string _cpuInfoPath;
    public LinuxHardware(string cpuInfoPath = "/proc/cpuinfo") => _cpuInfoPath = cpuInfoPath;

    public Task<CpuInfo> DetectCpuAsync(IHardwareProbe probe, CancellationToken ct)
    {
        try { return Task.FromResult(LinuxParsers.ParseCpuInfo(File.Exists(_cpuInfoPath) ? File.ReadAllText(_cpuInfoPath) : "")); }
        catch { return Task.FromResult(new CpuInfo()); }
    }

    public async Task<List<GpuInfo>> DetectGpusAsync(IHardwareProbe probe, CancellationToken ct)
    {
        var lspci = probe.RunAsync("lspci", new[] { "-nn" }, ct);
        var smi = probe.RunAsync("nvidia-smi", SmiArgs, ct);
        return HardwareParsers.Merge(LinuxParsers.ParseLspci(await lspci.ConfigureAwait(false)), HardwareParsers.ParseNvidiaSmi(await smi.ConfigureAwait(false)));
    }
}

public static class LinuxParsers
{
    /// <summary>/proc/cpuinfo: first "model name", logical threads = number of "processor" lines, cores = unique (physical id, core id) pairs.</summary>
    public static CpuInfo ParseCpuInfo(string text)
    {
        var cpu = new CpuInfo();
        var cores = new HashSet<string>();
        string phys = "0";
        var threads = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var i = raw.IndexOf(':');
            if (i < 0) continue;
            var key = raw[..i].Trim(); var val = raw[(i + 1)..].Trim();
            if (key == "processor") threads++;
            else if (key == "model name" && cpu.Name.Length == 0) cpu.Name = Regex.Replace(val, @"\s+", " ");
            else if (key == "physical id") phys = val;
            else if (key == "core id") cores.Add(phys + "/" + val);
        }
        cpu.LogicalThreads = threads;
        cpu.PhysicalCores = cores.Count > 0 ? cores.Count : threads;
        return cpu;
    }

    /// <summary>"lspci -nn": display controllers (class 0300 VGA, 0302 3D, 0380 display). Vendor comes from the PCI vendor id.</summary>
    public static List<GpuInfo> ParseLspci(string text)
    {
        var list = new List<GpuInfo>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(line, @"^\S+\s+(?<cls>[^\[]*)\[(?<c>0300|0302|0380)\]:\s*(?<rest>.*?)\s*\[(?<ven>[0-9a-fA-F]{4}):(?<dev>[0-9a-fA-F]{4})\](\s*\(rev [^)]*\))?$");
            if (!m.Success) continue;
            var vendor = m.Groups["ven"].Value.ToLowerInvariant() switch { "10de" => "NVIDIA", "1002" => "AMD", "8086" => "Intel", _ => "Other" };
            var rest = m.Groups["rest"].Value;
            var br = Regex.Match(rest, @"\[([^\]]+)\]\s*$");
            var name = br.Success ? br.Groups[1].Value : Regex.Replace(rest, @"\s*(Corporation|Inc\.?|Co\.?|Ltd\.?)\b", "").Trim();
            if (vendor != "Other" && !name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) name = vendor + " " + name;
            if (Regex.IsMatch(name, @"Virtual|QXL|VMware|Bochs|Cirrus", RegexOptions.IgnoreCase)) continue;
            list.Add(new GpuInfo { Name = name, Vendor = vendor });
        }
        return list;
    }
}
