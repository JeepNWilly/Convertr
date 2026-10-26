// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

namespace Convertr.Core;

/// <summary>The subset of settings the planner needs (thresholds, mode, subtitle and metadata options).</summary>
public sealed record PlannerOptions
{
    public int Kbps1080 { get; init; } = 4000;
    public int Kbps720 { get; init; } = 2500;
    public int KbpsSd { get; init; } = 1500;
    public int KbpsBelowSd { get; init; } = 1200;
    public EncoderMode Mode { get; init; } = EncoderMode.Gpu;
    /// <summary>Opt-in: process HDR, Dolby Vision and 4K files instead of sending them to Review (rule 2).</summary>
    public bool AllowHdr4k { get; init; }
    /// <summary>Default on: interlaced files are planned like any other (and flagged for deinterlacing with bwdif in conversion). Off: they go to Review (rule 2).</summary>
    public bool ProcessInterlaced { get; init; } = true;
    public bool KeepEmbeddedSubsIfNoExternal { get; init; }
    public bool StripSubsOnSkipped { get; init; }
    public bool CleanJunkMetadata { get; init; } = true;
    public bool CleanMetadataOnSkipped { get; init; }
    public bool KeepSensibleTitle { get; init; } = true;
    public bool PreferAacForSurround { get; init; }
    /// <summary>Quality settings and the keep-if-smaller rule feed the savings model (SavingsModel).</summary>
    public int CpuCrf { get; init; } = 21;
    public int GpuCq { get; init; } = 27;
    /// <summary>Intel GPU estimate inputs (the Linux service). Nvenc (the default) keeps the NVENC model, so the Windows estimates do not change.</summary>
    public GpuBackend GpuBackend { get; init; } = GpuBackend.Nvenc;
    public GpuCodec GpuCodec { get; init; } = GpuCodec.Hevc;
    public int IntelCq { get; init; } = 23;
    public int IntelQsvCq { get; init; } = 21;
    public int KeepPercent { get; init; } = 15;
    public int KeepMb { get; init; } = 100;
    public IReadOnlyList<string> JunkPatterns { get; init; } = Settings.DefaultJunkPatterns;

    public int ThresholdFor(ResolutionClass r) => r switch
    {
        ResolutionClass.P1080 or ResolutionClass.K4 => Kbps1080,
        ResolutionClass.P720 => Kbps720,
        ResolutionClass.P576 or ResolutionClass.P480 => KbpsSd,
        _ => KbpsBelowSd,
    };
}
