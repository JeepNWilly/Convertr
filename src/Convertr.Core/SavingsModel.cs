// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.


namespace Convertr.Core;

/// <summary>
/// Fitted size model for re-encodes: the video bitrate that the encoder produces depends on the content, not on the quality of the source,
/// so a source that is already lean barely shrinks (or grows) while a bloated one shrinks a lot. Measured on 30 to 90 second clips of real
/// library files (docs/RESULTS_SAVINGS.md): output kbps = A * (source kbps per 720p-sized frame) ^ B, per 720p-sized frame, for x265 slow CRF 21 and
/// NVENC (Pascal) p7 CQ 27. Quality settings change the result by a fixed factor per step.
/// </summary>
public sealed record SavingsCalibration(double A, double B, double OptimisticFactor, double PessimisticFactor, double SizeFactorPerThreeSteps);

public readonly record struct SavingsEstimate(long LowBytes, long MidBytes, long HighBytes, bool LikelyRefused);

public static class SavingsModel
{
    public const int CpuReferenceCrf = 21;
    public const int GpuReferenceCq = 27;

    /// <summary>x265 slow, CRF 21 (see docs/RESULTS_SAVINGS.md for the fit).</summary>
    public static readonly SavingsCalibration Cpu = new(31.3, 0.482, 0.71, 1.42, 0.61);
    /// <summary>NVENC hevc p7 tune hq spatial-aq lookahead 32, CQ 27.</summary>
    public static readonly SavingsCalibration Gpu = new(97.8, 0.387, 0.81, 1.23, 0.64);

    // ---- Intel Arc Pro B60 (VAAPI / QSV HEVC, ICQ). Measured with tools/intel-bench.sh on 5 movies, 30 s clips, VMAF against the source clip. ----
    // Same clips, size as % of the source: CPU x265 CRF 21 slow 36.4 % (VMAF 96.97); VAAPI q20 48.2 / q23 29.6 (95.66) / q26 28.9 / q29 18.0; QSV q21 30.3 (95.69) / q24 21.5 / q27 18.2.
    /// <summary>VAAPI HEVC at the reference quality (q23) produced 29.6 % of the source where CPU CRF 21 produced 36.4 %: 0.813 of the CPU model.</summary>
    public const double IntelVaapiRatioToCpu = 29.6 / 36.4;
    public const int IntelVaapiReferenceQ = 23;
    /// <summary>QSV HEVC at q21 produced 30.3 % (same picture quality as VAAPI q23): 0.832 of the CPU model.</summary>
    public const double IntelQsvRatioToCpu = 30.3 / 36.4;
    public const int IntelQsvReferenceQ = 21;
    /// <summary>Size factor per 3 quality steps (log fit over VAAPI q20..q29: 48.2 / 29.6 / 28.9 / 18.0 %). The q26 point is flat in the data, so this is approximate.</summary>
    public const double IntelSizeFactorPerThreeSteps = 0.74;

    /// <summary>True when the estimate for these options comes from the Intel model (GPU mode with the VAAPI or QSV backend).</summary>
    public static bool UsesIntelModel(PlannerOptions o) => o.Mode == EncoderMode.Gpu && o.GpuBackend is GpuBackend.Vaapi or GpuBackend.Qsv;

    /// <summary>The Intel model is measured for HEVC only; AV1 (opt-in) has no measurements, its estimate is a guess.</summary>
    public static bool IntelEstimateMeasured(PlannerOptions o) => o.GpuCodec == GpuCodec.Hevc;

    /// <summary>Expected Intel HEVC output bitrate: the CPU model (content dependent) times the measured Intel/CPU size ratio, scaled for the quality value.</summary>
    public static double IntelOutputKbps(double sourceKbps, int width, int height, GpuBackend backend, int quality, int spread = 0)
    {
        var cpu = OutputKbps(sourceKbps, width, height, EncoderMode.Cpu, CpuReferenceCrf, spread);
        var qsv = backend == GpuBackend.Qsv;
        var ratio = qsv ? IntelQsvRatioToCpu : IntelVaapiRatioToCpu;
        var refQ = qsv ? IntelQsvReferenceQ : IntelVaapiReferenceQ;
        return cpu * ratio * Math.Pow(IntelSizeFactorPerThreeSteps, (quality - refQ) / 3.0);
    }

    /// <summary>Expected output video bitrate (kbps) for a source of the given size class and bitrate. spread: 0 = expected, -1 = optimistic, +1 = pessimistic.</summary>
    public static double OutputKbps(double sourceKbps, int width, int height, EncoderMode mode, int quality, int spread = 0)
    {
        var c = mode == EncoderMode.Gpu ? Gpu : Cpu;
        var pixels = Math.Max(0.05, width * (double)height / (1280.0 * 720.0));
        var perFrame = Math.Max(1.0, sourceKbps / pixels);
        var refQ = mode == EncoderMode.Gpu ? GpuReferenceCq : CpuReferenceCrf;
        var qScale = Math.Pow(c.SizeFactorPerThreeSteps, (quality - refQ) / 3.0);
        var spreadScale = spread < 0 ? c.OptimisticFactor : spread > 0 ? c.PessimisticFactor : 1.0;
        return c.A * Math.Pow(perFrame, c.B) * pixels * qScale * spreadScale;
    }

    /// <summary>The keep-if-smaller rule of the verifier, as a pure function (used to predict which files would be refused).</summary>
    public static bool WouldKeep(long savedBytes, long sourceBytes, int keepPercent, int keepMb) =>
        savedBytes > 0 && sourceBytes > 0 && savedBytes * 100.0 >= keepPercent * (double)sourceBytes && savedBytes >= keepMb * 1048576L;

    /// <summary>Optimistic, expected and pessimistic saving of the whole file. A bound that would fail the keep rule counts as 0 (the job would be refused and nothing is saved).</summary>
    public static SavingsEstimate Estimate(long sourceKbps, int width, int height, double durationSec, long sizeBytes, PlannerOptions o)
    {
        var intel = UsesIntelModel(o);
        var q = intel ? (o.GpuBackend == GpuBackend.Qsv ? o.IntelQsvCq : o.IntelCq) : o.Mode == EncoderMode.Gpu ? o.GpuCq : o.CpuCrf;
        var srcVideoBytes = Math.Min((double)sizeBytes, sourceKbps * 125.0 * durationSec);
        long Saving(int spread)
        {
            // fraction of the video that is left; applied to the video part of the file only (audio and subtitles stay)
            var outKbps = intel ? IntelOutputKbps(sourceKbps, width, height, o.GpuBackend, q, spread) : OutputKbps(sourceKbps, width, height, o.Mode, q, spread);
            var left = outKbps / Math.Max(1.0, sourceKbps);
            var saved = (long)Math.Round(srcVideoBytes * (1.0 - left));
            return WouldKeep(saved, sizeBytes, o.KeepPercent, o.KeepMb) ? saved : 0;
        }
        var hi = Saving(-1);
        var mid = Saving(0);
        var lo = Saving(+1);
        return new SavingsEstimate(lo, mid, hi, mid == 0);
    }
}
