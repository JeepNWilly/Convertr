// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertr.Core;

public sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public int PhysicalCores { get; set; }
    public int LogicalThreads { get; set; }
    public override string ToString() => $"{Name} ({PhysicalCores} cores, {LogicalThreads} threads)";
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    /// <summary>"NVIDIA", "AMD", "Intel" or "Other".</summary>
    public string Vendor { get; set; } = "Other";
    /// <summary>0 = unknown. WMI reports at most 4 GB, so only nvidia-smi gives a trustworthy number for big cards.</summary>
    public long VramMb { get; set; }
    /// <summary>The driver version people know (NVIDIA: 581.80), not the long Windows version.</summary>
    public string Driver { get; set; } = "";
    public string WindowsDriver { get; set; } = "";
    public override string ToString() =>
        $"{Name}" + (VramMb > 0 ? $", {VramMb / 1024.0:0.#} GB" : "") + (Driver.Length > 0 ? $", driver {Driver}" : "");
}

/// <summary>The result of really starting the hardware encoders with the found ffmpeg.</summary>
public sealed class EncoderTests
{
    public bool Tested { get; set; }
    public bool NvencHevc { get; set; }
    public bool NvencH264 { get; set; }
    /// <summary>Two NVENC sessions at the same time worked (so two parallel GPU jobs are possible).</summary>
    public bool NvencTwoSessions { get; set; }
    /// <summary>Empty when it works; otherwise a readable reason with a suggestion.</summary>
    public string NvencReason { get; set; } = "";
    /// <summary>Info only: "works", "not available" or "no Intel GPU found".</summary>
    public string Qsv { get; set; } = "";
    public string Amf { get; set; } = "";
}

/// <summary>What was found on this computer. Saved in settings.json with the time of detection.</summary>
public sealed class HardwareProfile
{
    public DateTime DetectedUtc { get; set; }
    /// <summary>Name of the computer that was measured. A profile from another computer is never trusted: detection runs again.</summary>
    public string MachineName { get; set; } = "";
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public string FfmpegVersion { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public EncoderTests Encoders { get; set; } = new();
    /// <summary>Intel (VAAPI / QSV) test results; only filled by Convertr_svc.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IntelCapabilities? Intel { get; set; }
    /// <summary>The decision that was made from this profile (shown in Setup).</summary>
    public string DefaultsSummary { get; set; } = "";
    public string DefaultsWhy { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasNvidia => Gpus.Any(g => g.Vendor == "NVIDIA");

    /// <summary>GPU names, drivers and the ffmpeg version: when this changes the hardware is detected again.</summary>
    public string Signature() => Signature(Gpus, FfmpegVersion);

    public static string Signature(IEnumerable<GpuInfo> gpus, string ffmpegVersion) =>
        string.Join(";", gpus.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Name.Trim() + "|" + g.Driver.Trim())) + "#" + ffmpegVersion.Trim();
}

/// <summary>Parsers for the text that Get-CimInstance (as JSON) and nvidia-smi (as CSV) print. Pure, so they can be tested with samples.</summary>
public static class HardwareParsers
{
    private static IEnumerable<JsonElement> Items(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF')); } catch (JsonException) { yield break; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) { foreach (var e in root.EnumerateArray()) yield return e.Clone(); }
            else if (root.ValueKind == JsonValueKind.Object) yield return root.Clone();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
    }

    /// <summary>Win32_Processor as JSON (Name, NumberOfCores, NumberOfLogicalProcessors); several sockets are added up.</summary>
    public static CpuInfo ParseCpu(string json)
    {
        var cpu = new CpuInfo();
        foreach (var e in Items(json))
        {
            if (cpu.Name.Length == 0) cpu.Name = Regex.Replace(Str(e, "Name").Trim(), @"\s+", " ");
            cpu.PhysicalCores += (int)Num(e, "NumberOfCores");
            cpu.LogicalThreads += (int)Num(e, "NumberOfLogicalProcessors");
        }
        return cpu;
    }

    /// <summary>Win32_VideoController as JSON (Name, AdapterRAM, DriverVersion). Virtual and remote adapters are ignored.</summary>
    public static List<GpuInfo> ParseGpus(string json)
    {
        var list = new List<GpuInfo>();
        foreach (var e in Items(json))
        {
            var name = Str(e, "Name").Trim();
            if (name.Length == 0 || Regex.IsMatch(name, @"Microsoft Basic|Remote Display|Virtual|Hyper-V|Parsec|Meta Virtual", RegexOptions.IgnoreCase)) continue;
            var win = Str(e, "DriverVersion").Trim();
            var vendor = VendorOf(name);
            var ram = Num(e, "AdapterRAM");
            // AdapterRAM is a 32-bit value: negative or 4 GB-ish means "more than 4 GB, unknown".
            var vram = ram > 0 && ram < 4_000_000_000L ? ram / 1048576 : 0;
            list.Add(new GpuInfo { Name = name, Vendor = vendor, VramMb = vram, WindowsDriver = win, Driver = vendor == "NVIDIA" ? NvidiaDriverFromWindows(win) : win });
        }
        return list;
    }

    public static string VendorOf(string name) =>
        Regex.IsMatch(name, @"nvidia|geforce|quadro|rtx|gtx|tesla", RegexOptions.IgnoreCase) ? "NVIDIA" :
        Regex.IsMatch(name, @"\bamd\b|radeon|\bati\b", RegexOptions.IgnoreCase) ? "AMD" :
        Regex.IsMatch(name, @"intel|\barc\b|uhd graphics|iris", RegexOptions.IgnoreCase) ? "Intel" : "Other";

    /// <summary>The Windows driver version 32.0.15.8180 is NVIDIA driver 581.80 (the last five digits).</summary>
    public static string NvidiaDriverFromWindows(string win)
    {
        var digits = new string((win ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return win ?? "";
        var last5 = digits[^5..];
        return $"{last5[..3].TrimStart('0').PadLeft(3, '0')}.{last5[3..]}";
    }

    /// <summary>nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits</summary>
    public static List<GpuInfo> ParseNvidiaSmi(string csv)
    {
        var list = new List<GpuInfo>();
        foreach (var raw in (csv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !Regex.IsMatch(parts[1], @"^\d+(\.\d+)*$")) continue;
            _ = long.TryParse(Regex.Match(parts[2], @"\d+").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb);
            list.Add(new GpuInfo { Name = parts[0], Vendor = "NVIDIA", Driver = parts[1], VramMb = mb });
        }
        return list;
    }

    /// <summary>Merge: nvidia-smi gives the exact name, driver and memory of NVIDIA cards; the CIM list supplies the others.</summary>
    public static List<GpuInfo> Merge(List<GpuInfo> cim, List<GpuInfo> smi)
    {
        var result = new List<GpuInfo>();
        var smiLeft = new List<GpuInfo>(smi);
        foreach (var g in cim)
        {
            if (g.Vendor == "NVIDIA" && smiLeft.Count > 0)
            {
                var m = smiLeft.FirstOrDefault(x => g.Name.Contains(x.Name.Replace("NVIDIA ", ""), StringComparison.OrdinalIgnoreCase) || x.Name.Contains(g.Name.Replace("NVIDIA ", ""), StringComparison.OrdinalIgnoreCase)) ?? smiLeft[0];
                smiLeft.Remove(m);
                result.Add(new GpuInfo { Name = g.Name, Vendor = "NVIDIA", VramMb = m.VramMb > 0 ? m.VramMb : g.VramMb, Driver = m.Driver, WindowsDriver = g.WindowsDriver });
            }
            else result.Add(g);
        }
        result.AddRange(smiLeft);   // nvidia-smi sees a card that CIM did not list
        return result;
    }
}

/// <summary>Runs a program and returns its text (replaced by a fake in tests).</summary>
public interface IHardwareProbe
{
    Task<string> RunAsync(string exe, string[] args, CancellationToken ct);
}

public sealed class ProcessHardwareProbe : IHardwareProbe
{
    public async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var exeName = Path.GetFileNameWithoutExtension(exe);
        if (exeName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || exeName.Equals("ffprobe", StringComparison.OrdinalIgnoreCase)) FfmpegEnvironment.Apply(psi);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(20));
            try { await p.WaitForExitAsync(t.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* gone */ } return ""; }
            return await o.ConfigureAwait(false);
        }
        catch { return ""; }   // program not installed
    }
}

public static class HardwareDetector
{
    public const string CpuQuery = "Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors | ConvertTo-Json -Compress";
    public const string GpuQuery = "Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,DriverVersion | ConvertTo-Json -Compress";
    private static readonly string[] SmiArgs = { "--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits" };

    private static string[] Ps(string command) => new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command };

    public static async Task<List<GpuInfo>> DetectGpusAsync(IHardwareProbe probe, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return new();
        return await PlatformServices.Current.Hardware.DetectGpusAsync(probe, ct).ConfigureAwait(false);
    }

    /// <summary>Cheap check done at start-up: has the GPU, its driver or ffmpeg changed since the saved profile?</summary>
    public static async Task<bool> HasChangedAsync(HardwareProfile saved, FfmpegLocation? loc, IHardwareProbe probe, CancellationToken ct = default, string? machineName = null)
    {
        var gpus = await DetectGpusAsync(probe, ct).ConfigureAwait(false);
        var ver = "";
        if (loc != null) ver = (await FfmpegLocator.DetectAsync(loc, ct).ConfigureAwait(false))?.Version ?? "";
        if (HardwareProfile.Signature(gpus, ver) != saved.Signature()) return true;
        return await ProfileIsFromAnotherComputerAsync(saved, probe, machineName ?? Environment.MachineName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the saved profile was made on another computer: a different computer name, or (for profiles saved before the name was
    /// recorded) a different processor name.
    /// </summary>
    public static async Task<bool> ProfileIsFromAnotherComputerAsync(HardwareProfile saved, IHardwareProbe probe, string machineName, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(saved.MachineName))
            return !string.Equals(saved.MachineName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return false;
        var cpu = await PlatformServices.Current.Hardware.DetectCpuAsync(probe, ct).ConfigureAwait(false);
        return cpu.Name.Length > 0 && saved.Cpu.Name.Length > 0 && !string.Equals(cpu.Name.Trim(), saved.Cpu.Name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full detection: CPU, GPUs, driver, then really tries the hardware encoders (about 3 seconds).</summary>
    public static async Task<HardwareProfile> DetectAsync(FfmpegLocation? loc, IHardwareProbe? probe = null, CancellationToken ct = default)
    {
        probe ??= new ProcessHardwareProbe();
        var hw = new HardwareProfile { DetectedUtc = DateTime.UtcNow, MachineName = Environment.MachineName };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            hw.Cpu = await PlatformServices.Current.Hardware.DetectCpuAsync(probe, ct).ConfigureAwait(false);
            hw.Gpus = await DetectGpusAsync(probe, ct).ConfigureAwait(false);
        }
        if (hw.Cpu.LogicalThreads == 0) { hw.Cpu.LogicalThreads = Environment.ProcessorCount; if (hw.Cpu.PhysicalCores == 0) hw.Cpu.PhysicalCores = Math.Max(1, Environment.ProcessorCount / 2); }
        if (hw.Cpu.Name.Length == 0) hw.Cpu.Name = "Unknown CPU";

        if (loc != null && !string.IsNullOrEmpty(loc.FfmpegPath))
        {
            hw.FfmpegPath = loc.FfmpegPath;
            var caps = await FfmpegLocator.DetectAsync(loc, ct).ConfigureAwait(false);
            hw.FfmpegVersion = caps?.Version ?? "";
            await TestEncodersAsync(hw, loc, caps, ct).ConfigureAwait(false);
        }
        else hw.Encoders = new EncoderTests { Tested = false, NvencReason = "ffmpeg was not found, so the GPU encoder could not be tested. Press Download FFmpeg in Setup." };
        return hw;
    }

    private static async Task TestEncodersAsync(HardwareProfile hw, FfmpegLocation loc, FfmpegCapabilities? caps, CancellationToken ct)
    {
        var t = new EncoderTests { Tested = true };
        if (!hw.HasNvidia) t.NvencReason = "no NVIDIA GPU was found. Use CPU mode.";
        else if (caps != null && !caps.NvencHevc) t.NvencReason = "this ffmpeg build has no NVENC encoder. Press Download FFmpeg to get a full build.";
        else
        {
            var (ok, msg) = await FfmpegLocator.TestNvencAsync(loc, true, ct).ConfigureAwait(false);
            t.NvencHevc = ok;
            if (!ok) t.NvencReason = msg;
            else
            {
                t.NvencH264 = (await FfmpegLocator.TestNvencAsync(loc, false, ct).ConfigureAwait(false)).Ok;
                var two = await Task.WhenAll(FfmpegLocator.TestNvencAsync(loc, true, ct), FfmpegLocator.TestNvencAsync(loc, true, ct)).ConfigureAwait(false);
                t.NvencTwoSessions = two.All(x => x.Ok);
            }
        }
        t.Qsv = await InfoTestAsync(hw, loc, "Intel", "hevc_qsv", ct).ConfigureAwait(false);
        t.Amf = await InfoTestAsync(hw, loc, "AMD", "hevc_amf", ct).ConfigureAwait(false);
        hw.Encoders = t;
    }

    private static async Task<string> InfoTestAsync(HardwareProfile hw, FfmpegLocation loc, string vendor, string encoder, CancellationToken ct)
    {
        if (!hw.Gpus.Any(g => g.Vendor == vendor)) return $"no {vendor} GPU found";
        var (ok, _) = await FfmpegLocator.TestEncoderAsync(loc, encoder, ct).ConfigureAwait(false);
        return ok ? "works (not used by Convertr yet)" : "not available";
    }
}

/// <summary>Readable text for the Setup "Hardware" section and the harness (one place, so tests can check it).</summary>
public static class HardwareReport
{
    public static string NvencStatus(HardwareProfile hw)
    {
        var e = hw.Encoders;
        if (!e.Tested && !hw.HasNvidia) return "not available: no NVIDIA GPU found";
        if (!e.Tested) return "not tested yet: " + e.NvencReason;
        if (e.NvencHevc)
            return "works (HEVC" + (e.NvencH264 ? " and H.264" : "") + "; " + (e.NvencTwoSessions ? "2 sessions at once" : "1 session at a time") + ")";
        return "not available: " + e.NvencReason;
    }

    public static string When(HardwareProfile hw) =>
        hw.DetectedUtc == default ? "never" : hw.DetectedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string Describe(HardwareProfile? hw)
    {
        if (hw == null) return "Not detected yet. Press \"Re-detect hardware\".";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CPU: " + hw.Cpu);
        if (hw.Gpus.Count == 0) sb.AppendLine("GPU: none found");
        foreach (var g in hw.Gpus) sb.AppendLine("GPU: " + g);
        sb.AppendLine("ffmpeg: " + (hw.FfmpegVersion.Length > 0 ? hw.FfmpegVersion : "not found"));
        sb.AppendLine("GPU encoder (NVENC): " + NvencStatus(hw));
        if (hw.Encoders.Qsv.Length > 0) sb.AppendLine("Intel QSV: " + hw.Encoders.Qsv + "   AMD AMF: " + hw.Encoders.Amf);
        sb.AppendLine("Chosen defaults: " + hw.DefaultsSummary);
        sb.AppendLine("Why: " + hw.DefaultsWhy);
        sb.Append("Detected: " + When(hw));
        return sb.ToString();
    }
}
