// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>How the hardware-based defaults are chosen (Setup, Hardware section).</summary>
public enum DefaultsPolicy
{
    /// <summary>Recommended: x265 on the CPU for the smallest files; the GPU only when the CPU is weak.</summary>
    Auto,
    /// <summary>Always the CPU (x265): slowest, smallest files, best quality per byte.</summary>
    PreferCpuQuality,
    /// <summary>The GPU (NVENC) whenever it works: fastest, but the files stay clearly bigger.</summary>
    PreferGpuSpeed,
}

/// <summary>The settings that the hardware decision may change, and the names used to remember that the user changed them by hand.</summary>
public static class TunedSettings
{
    public const string Mode = "Mode", GpuCq = "GpuCq", X265Preset = "X265Preset", ParallelGpuJobs = "ParallelGpuJobs",
        ParallelCpuJobs = "ParallelCpuJobs", X265Threads = "X265Threads";

    public static readonly string[] All = { Mode, GpuCq, X265Preset, ParallelGpuJobs, ParallelCpuJobs, X265Threads };

    public static object? Get(Settings s, string key) => key switch
    {
        Mode => s.Mode, GpuCq => s.GpuCq, X265Preset => s.X265Preset, ParallelGpuJobs => s.ParallelGpuJobs,
        ParallelCpuJobs => s.ParallelCpuJobs, X265Threads => s.X265Threads, _ => null,
    };

    /// <summary>Marks every tuned setting whose value differs between "before" and "after" as changed by the user.</summary>
    public static List<string> MarkUserChanges(Settings before, Settings after)
    {
        var marked = new List<string>();
        foreach (var k in All)
            if (!Equals(Get(before, k), Get(after, k)) && !after.UserModified.Contains(k)) { after.UserModified.Add(k); marked.Add(k); }
        return marked;
    }
}

public sealed record DefaultsDecision(EncoderMode Mode, int GpuCq, string X265Preset, int ParallelCpuJobs, int ParallelGpuJobs, int X265Threads, string Summary, string Why);

/// <summary>
/// Chooses encoder defaults from the detected hardware (pure, tested with mocked profiles). Measured on the development machine
/// (i7-7700K, GTX 1080 Ti): x265 CRF 21 gives files about 2x smaller than NVENC at the same VMAF, NVENC is 7 to 18 times faster than real
/// time and x265 slow about 1 time. NVENC CQ 27 gives 51-58% of the source at VMAF 95.4-96.8, so 27 is the GPU default, not 24.
/// </summary>
public static class HardwareDefaults
{
    public static DefaultsDecision Decide(HardwareProfile hw, DefaultsPolicy policy)
    {
        var threads = Math.Max(1, hw.Cpu.LogicalThreads);
        var nvenc = hw.Encoders.NvencHevc;
        var weakCpu = threads < 4;
        var why = new List<string>();

        EncoderMode mode;
        switch (policy)
        {
            case DefaultsPolicy.PreferCpuQuality:
                mode = EncoderMode.Cpu;
                why.Add("Policy 'Prefer CPU quality': x265 on the CPU gives the smallest files");
                break;
            case DefaultsPolicy.PreferGpuSpeed:
                mode = nvenc ? EncoderMode.Gpu : EncoderMode.Cpu;
                why.Add(nvenc ? "Policy 'Prefer GPU speed': NVENC works, so the GPU is used (fast, but files stay larger)"
                              : "Policy 'Prefer GPU speed' wanted the GPU, but NVENC is not available (see above), so the CPU is used");
                break;
            default:
                if (weakCpu && nvenc) { mode = EncoderMode.Gpu; why.Add($"The CPU has only {threads} threads, so the GPU (NVENC) is faster in practice"); }
                else
                {
                    mode = EncoderMode.Cpu;
                    why.Add("x265 on the CPU gives files about 2 times smaller than NVENC at the same quality" +
                            (nvenc ? "; the GPU is much faster (about 7 to 18 times real time against about 1) but compresses less. Choose 'Prefer GPU speed' to use it"
                                   : "; GPU encoding (NVENC) is not available here (see the NVENC line above)"));
                }
                break;
        }

        var preset = threads >= 6 ? "slow" : "medium";
        why.Add($"x265 preset {preset} ({threads} threads{(threads >= 6 ? "" : ": a faster preset keeps the queue moving")})");
        var gpuJobs = nvenc && hw.Encoders.NvencTwoSessions ? 2 : 1;
        if (nvenc) why.Add(gpuJobs == 2 ? "two NVENC sessions started together, so up to 2 parallel GPU jobs" : "only one NVENC session worked at a time, so 1 GPU job");
        why.Add("1 CPU job at a time (x265 already uses all threads), GPU CQ 27 (51-58% of the source at VMAF about 96 in tests)");

        var summary = $"{(mode == EncoderMode.Cpu ? "CPU (x265)" : "GPU (NVENC)")}, x265 preset {preset}, CPU jobs 1, GPU jobs {gpuJobs}, GPU CQ 27";
        return new DefaultsDecision(mode, 27, preset, 1, gpuJobs, 0, summary, string.Join(". ", why) + ".");
    }

    /// <summary>Applies the decision to the settings, except for settings the user changed by hand. Returns the names that were changed.</summary>
    public static List<string> Apply(Settings s, DefaultsDecision d, HardwareProfile hw)
    {
        var applied = new List<string>();
        void Set<T>(string key, T value, Action<T> set)
        {
            if (s.UserModified.Contains(key)) return;
            if (!Equals(TunedSettings.Get(s, key), value)) applied.Add(key);
            set(value);
        }
        Set(TunedSettings.Mode, d.Mode, v => s.Mode = v);
        Set(TunedSettings.GpuCq, d.GpuCq, v => s.GpuCq = v);
        Set(TunedSettings.X265Preset, d.X265Preset, v => s.X265Preset = v);
        Set(TunedSettings.ParallelCpuJobs, d.ParallelCpuJobs, v => s.ParallelCpuJobs = v);
        Set(TunedSettings.ParallelGpuJobs, d.ParallelGpuJobs, v => s.ParallelGpuJobs = v);
        Set(TunedSettings.X265Threads, d.X265Threads, v => s.X265Threads = v);
        hw.DefaultsSummary = d.Summary + (s.UserModified.Count > 0 ? $" (kept your own choices for: {string.Join(", ", s.UserModified.Where(k => TunedSettings.All.Contains(k)))})" : "");
        hw.DefaultsWhy = d.Why;
        s.Hardware = hw;
        return applied;
    }

    /// <summary>"Reset to recommended": forget the manual choices, then apply.</summary>
    public static List<string> ApplyAll(Settings s, DefaultsDecision d, HardwareProfile hw)
    {
        s.UserModified.Clear();
        return Apply(s, d, hw);
    }
}

/// <summary>Rough time estimate for a queue. Uses the benchmark of the chosen mode when there is one, otherwise typical speeds.</summary>
public static class SpeedEstimator
{
    /// <summary>x-realtime for a full-quality re-encode, from the benchmark or a typical value for the mode and CPU size.</summary>
    public static (double X, bool Measured) EncodeSpeed(Settings s)
    {
        var bench = s.Mode == EncoderMode.Gpu ? s.BenchmarkGpuX : s.BenchmarkCpuX;
        if (bench is double b && b > 0) return (b, true);
        if (s.Mode == EncoderMode.Gpu) return (10.0, false);   // measured 7-18x on a GTX 1080 Ti
        var threads = s.Hardware?.Cpu.LogicalThreads is int t and > 0 ? t : 8;
        var presetFactor = s.X265Preset is "medium" ? 2.0 : s.X265Preset is "fast" or "faster" ? 3.5 : 1.0;
        return (Math.Max(0.2, 1.0 * threads / 8.0) * presetFactor, false);   // measured about 1x for x265 slow on 4 cores / 8 threads
    }

    public static string Format(double hours) =>
        hours < 1.0 / 60 ? "under a minute" : hours < 1 ? $"{Math.Round(hours * 60):0} minutes" : hours < 48 ? $"{hours:0.#} hours" : $"{hours / 24:0.#} days";

    /// <summary>Hours for the jobs: re-encodes run at the encode speed , remuxes and audio fixes at 150x.</summary>
    public static double EstimateHours(IEnumerable<(PlanItem Plan, double DurationSec)> jobs, Settings s)
    {
        var (x, _) = EncodeSpeed(s);
        double seconds = 0;
        foreach (var (plan, dur) in jobs)
        {
            if (dur <= 0) continue;
            seconds += plan.Action == PlanAction.ReencodeVideo ? dur / x : dur / 150.0;
        }
        var par = s.Mode == EncoderMode.Gpu ? s.ParallelGpuJobs : s.ParallelCpuJobs;
        if (par > 1) seconds /= 1.6;   // parallel jobs share the same chip: about 1.6 times faster for two (estimate)
        return seconds / 3600.0;
    }
}
