// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Convertr.Core;

public sealed class LibraryFolder
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Every Setup option (DESIGN.md section 9: each accepted default is changeable). Saved to settings.json next to the exe.
/// Options marked "M2" are saved now but only take effect once conversion is enabled.
/// </summary>
public sealed class Settings
{
    public static readonly string[] DefaultJunkPatterns =
    {
        @"https?://",
        @"\bwww\.",
        @"\b[a-z0-9-]+\.(com|org|net|bz|to|io|me|cc|tv|ws)\b",
        @"\b(uindex|yts|yify|rarbg|eztv|ettv|tgx|1337x|torrent\w*)\b",
        @"\b(handbrake|mkvmerge|libebml|libmatroska|lavf\d*|ffmpeg|x26[45])\b",
        @"\b(encoded|ripped|downloaded|uploaded|released|subbed|synced|muxed)\s+(by|from)\b",
    };

    // ---- media folder ----
    /// <summary>The one folder that is scanned (recursively). Remembered between runs. Empty on a fresh install: there is no built-in path.</summary>
    public string MediaFolder { get; set; } = "";

    /// <summary>Name of the computer that last saved this file. Used to spot a settings file (or paths) that came from another computer.</summary>
    public string SavedOnMachine { get; set; } = "";

    /// <summary>Legacy (0.1.0 before the single "Media" box): the old Movies/Series list. Read once to migrate, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LibraryFolder>? Libraries { get; set; }

    // ---- appearance ----
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // ---- FFmpeg ----
    /// <summary>Folder with ffmpeg and ffprobe. Empty = auto-find (next to the exe, its ffmpeg subfolder, Convertr's download folder, then PATH).</summary>
    public string FfmpegFolder { get; set; } = "";
    public int ProbeThreads { get; set; } = 3;
    /// <summary>Include Plex's "Plex Versions" (optimized versions) folders in the scan. Off by default: Plex manages those copies.</summary>
    public bool IncludePlexVersions { get; set; } = false;

    // ---- encoder (mode affects estimates now; the rest is M2) ----
    public EncoderMode Mode { get; set; } = EncoderMode.Gpu;
    public string X265Preset { get; set; } = "slow";          // M2
    public int CpuCrf { get; set; } = 21;                      // M2
    public int GpuCq { get; set; } = 27;                       // 27 after the M2 measurements (24 made files as big as the source)
    public int LegacyCrf { get; set; } = 20;                   // M2
    public bool UseHevc10Bit { get; set; } = false;            // M2
    public int ParallelGpuJobs { get; set; } = 1;              // M2
    public int ParallelCpuJobs { get; set; } = 1;              // M2
    public string ProcessPriority { get; set; } = "BelowNormal"; // M2
    /// <summary>x265 thread pool size (0 = automatic). Used with several CPU jobs.</summary>
    public int X265Threads { get; set; } = 0;

    // ---- hardware (detected at the first launch and when Re-detect is pressed) ----
    public DefaultsPolicy DefaultsPolicy { get; set; } = DefaultsPolicy.Auto;
    /// <summary>Settings the user changed by hand (see TunedSettings): hardware detection never overwrites these.</summary>
    public List<string> UserModified { get; set; } = new();
    public HardwareProfile? Hardware { get; set; }
    /// <summary>Speed measured by the 20-second benchmark (times real time) and when.</summary>
    public double? BenchmarkCpuX { get; set; }
    public double? BenchmarkGpuX { get; set; }
    public DateTime? BenchmarkUtc { get; set; }

    /// <summary>Size of the Setup window (outer width and height in pixels) when the user last saved it; 0 = not set (use the default size).</summary>
    public int SetupWidth { get; set; } = 0;
    public int SetupHeight { get; set; } = 0;

    // ---- platform / server options (Convertr_svc DESIGN.md sections 5A, 7A). Additive: the defaults reproduce the desktop behaviour. ----
    /// <summary>GPU encoder API. The desktop app uses Nvenc; the other values are reserved for the Linux service.</summary>
    public GpuBackend GpuBackend { get; set; } = GpuBackend.Nvenc;
    public GpuCodec GpuCodec { get; set; } = GpuCodec.Hevc;
    /// <summary>Render node for the Intel (VAAPI / QSV) encoders. Empty = the first /dev/dri/renderD*.</summary>
    public string GpuDevice { get; set; } = "";
    /// <summary>Intel VAAPI HEVC quality (global_quality, ICQ): lower = better and bigger. 23 = measured default on the Arc Pro B60 (tools/intel-bench.sh, VMAF 95.7, about 30 % of the source size on 5 movies).</summary>
    public int IntelCq { get; set; } = 23;
    /// <summary>Intel QSV HEVC quality (ICQ with look-ahead). 21 gives the same picture quality and size as VAAPI 23 on the Arc Pro B60.</summary>
    public int IntelQsvCq { get; set; } = 21;
    /// <summary>Intel AV1 quality (opt-in codec). UNTESTED: not benchmarked yet, a placeholder.</summary>
    public int IntelAv1Cq { get; set; } = 27;
    /// <summary>Rate-control options for the Intel encoder, with {Q} for the quality value. Empty = the built-in default for the backend; filled in by the encoder test (what really works on this GPU).</summary>
    public string GpuEncoderArgs { get; set; } = "";
    /// <summary>NAME=VALUE pairs added to every ffmpeg / ffprobe process (empty = none). On Battlemage-class Intel GPUs: the system libva / iHD variables.</summary>
    public List<string> FfmpegEnvironment { get; set; } = new();
    /// <summary>Detect by test encode whether the bundled libva crashes and apply the system-libs environment (service only).</summary>
    public bool FfmpegEnvironmentAuto { get; set; } = false;
    /// <summary>Stage each job through a local scratch disk (copy in, encode, copy back). Off = in place, as the desktop app does.</summary>
    public bool UseScratch { get; set; } = false;
    public string ScratchFolder { get; set; } = "";
    /// <summary>0 = automatic (80 percent of the free space).</summary>
    public int ScratchBudgetGb { get; set; } = 0;
    /// <summary>0 = automatic (twice the largest planned source, at least 20 GB).</summary>
    public int MaxScratchPerJobGb { get; set; } = 0;
    public int MaxConcurrentTransfers { get; set; } = 2;
    public CopyBackVerify CopyBackVerify { get; set; } = CopyBackVerify.SizeAndProbe;
    public bool StopEncodeEarly { get; set; } = true;
    public bool KeepScratchOnFailure { get; set; } = false;
    public int NasOfflineRetryHours { get; set; } = 6;
    public int StaleScratchHours { get; set; } = 6;

    // ---- rules ----
    public int Kbps1080 { get; set; } = 4000;
    public int Kbps720 { get; set; } = 2500;
    public int KbpsSd { get; set; } = 1500;
    public int KbpsBelowSd { get; set; } = 1200;
    public int KeepIfSmallerPercent { get; set; } = 15;        // M2
    public int KeepIfSmallerMb { get; set; } = 100;            // M2
    public OutputContainer Container { get; set; } = OutputContainer.Mp4; // M2
    public bool AllowHdr4k { get; set; } = false;
    public bool ProcessInterlaced { get; set; } = true;

    // ---- audio (M2) ----
    public AudioFallback DtsOtherTarget { get; set; } = AudioFallback.Ac3;
    public bool KeepOriginalAudioAsSecondTrack { get; set; } = false;
    public bool AddAacCompatTrack { get; set; } = false;
    public bool KeepOnlyLanguages { get; set; } = false;
    public string KeepLanguages { get; set; } = "eng,und";

    // ---- subtitles (affect the preview through rule 7) ----
    public bool KeepEmbeddedSubsIfNoExternal { get; set; } = false;
    public bool StripSubsOnSkipped { get; set; } = false;

    // ---- metadata ----
    public bool CleanJunkMetadata { get; set; } = true;
    public bool CleanMetadataOnSkipped { get; set; } = false;
    public bool DropChapters { get; set; } = false;            // M2
    public bool KeepSensibleTitle { get; set; } = true;
    public bool KeepAudioTrackTitles { get; set; } = true;     // M2
    public List<string> JunkPatterns { get; set; } = new(DefaultJunkPatterns);

    // ---- output handling (M2/M3) ----
    public bool PreserveTimestamps { get; set; } = true;
    public OriginalsHandling Originals { get; set; } = OriginalsHandling.MoveToHolding;
    public string OriginalsFolderName { get; set; } = "_Convertr_Originals";
    public int PurgeAfterDays { get; set; } = 14;
    /// <summary>Stop starting new files once this many GB of source files were processed in one run. 0 = no limit (default).</summary>
    public int BatchLimitGb { get; set; } = 0;
    /// <summary>Decode a few slices of every output with ffmpeg before it replaces the original.</summary>
    public bool VerifyDecode { get; set; } = true;
    /// <summary>Do not show the summary dialog before a run (it is always shown the first time).</summary>
    public bool SkipRunSummary { get; set; } = false;
    /// <summary>Set once the first-run confirmation was accepted.</summary>
    public bool FirstRunConfirmed { get; set; } = false;
    /// <summary>Dry run: build and log the ffmpeg commands but never start ffmpeg or touch any file.</summary>
    public bool DryRun { get; set; } = false;
    public bool QuietHoursEnabled { get; set; } = false;
    public string QuietHoursStart { get; set; } = "01:00";
    public string QuietHoursEnd { get; set; } = "15:00";

    public PlannerOptions ToPlannerOptions() => new()
    {
        Kbps1080 = Kbps1080,
        Kbps720 = Kbps720,
        KbpsSd = KbpsSd,
        KbpsBelowSd = KbpsBelowSd,
        Mode = Mode,
        AllowHdr4k = AllowHdr4k,
        ProcessInterlaced = ProcessInterlaced,
        KeepEmbeddedSubsIfNoExternal = KeepEmbeddedSubsIfNoExternal,
        StripSubsOnSkipped = StripSubsOnSkipped,
        CleanJunkMetadata = CleanJunkMetadata,
        CleanMetadataOnSkipped = CleanMetadataOnSkipped,
        KeepSensibleTitle = KeepSensibleTitle,
        PreferAacForSurround = DtsOtherTarget == AudioFallback.Aac,
        CpuCrf = CpuCrf,
        GpuCq = GpuCq,
        GpuBackend = GpuBackend,
        GpuCodec = GpuCodec,
        IntelCq = IntelCq,
        IntelQsvCq = IntelQsvCq,
        KeepPercent = KeepIfSmallerPercent,
        KeepMb = KeepIfSmallerMb,
        JunkPatterns = JunkPatterns.Count == 0 ? DefaultJunkPatterns : JunkPatterns.ToArray(),
    };

    /// <summary>Clamp values to sane ranges after loading a hand-edited file.</summary>
    public void Normalize()
    {
        ProbeThreads = Math.Clamp(ProbeThreads, 1, 16);
        Kbps1080 = Math.Clamp(Kbps1080, 100, 100000);
        Kbps720 = Math.Clamp(Kbps720, 100, 100000);
        KbpsSd = Math.Clamp(KbpsSd, 100, 100000);
        KbpsBelowSd = Math.Clamp(KbpsBelowSd, 100, 100000);
        KeepIfSmallerPercent = Math.Clamp(KeepIfSmallerPercent, 0, 90);
        KeepIfSmallerMb = Math.Clamp(KeepIfSmallerMb, 0, 100000);
        CpuCrf = Math.Clamp(CpuCrf, 10, 40);
        GpuCq = Math.Clamp(GpuCq, 10, 40);
        IntelCq = Math.Clamp(IntelCq, 10, 45);
        IntelQsvCq = Math.Clamp(IntelQsvCq, 10, 45);
        LegacyCrf = Math.Clamp(LegacyCrf, 10, 40);
        ParallelGpuJobs = Math.Clamp(ParallelGpuJobs, 1, 4);
        ParallelCpuJobs = Math.Clamp(ParallelCpuJobs, 1, 4);
        PurgeAfterDays = Math.Clamp(PurgeAfterDays, 0, 3650);
        BatchLimitGb = Math.Clamp(BatchLimitGb, 0, 100000);
        MediaFolder = (MediaFolder ?? "").Trim();
        SavedOnMachine ??= "";
        JunkPatterns ??= new(DefaultJunkPatterns);
        X265Preset = string.IsNullOrWhiteSpace(X265Preset) ? "slow" : X265Preset;
        FfmpegFolder ??= "";
        FfmpegEnvironment ??= new();
        ScratchFolder = (ScratchFolder ?? "").Trim();
        ScratchBudgetGb = Math.Clamp(ScratchBudgetGb, 0, 1000000);
        MaxScratchPerJobGb = Math.Clamp(MaxScratchPerJobGb, 0, 1000000);
        MaxConcurrentTransfers = Math.Clamp(MaxConcurrentTransfers, 1, 8);
        NasOfflineRetryHours = Math.Clamp(NasOfflineRetryHours, 1, 168);
        StaleScratchHours = Math.Clamp(StaleScratchHours, 1, 168);
        UserModified ??= new();
        X265Threads = Math.Clamp(X265Threads, 0, 128);
        if (SetupWidth < 300 || SetupHeight < 300 || SetupWidth > 10000 || SetupHeight > 10000) { SetupWidth = 0; SetupHeight = 0; }
    }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath => PlatformServices.Current.Paths.SettingsPath;

    /// <summary>Loads settings; a missing or unreadable file gives defaults (an unreadable file is kept as .bad).</summary>
    public static Settings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Settings();
            var text = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(text, JsonOptions) ?? new Settings();
            MigrateLegacyLibraries(s, text);
            s.Normalize();
            return s;
        }
        catch
        {
            try { File.Copy(path, path + ".bad", true); } catch { /* ignore */ }
            return new Settings();
        }
    }

    /// <summary>Old files had a Libraries list (Movies, Series). Keep the Movies path (or the first path) as the single Media folder.</summary>
    internal static void MigrateLegacyLibraries(Settings s, string json)
    {
        bool hasMedia;
        using (var doc = JsonDocument.Parse(json))
            hasMedia = doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("MediaFolder", StringComparison.OrdinalIgnoreCase));
        if (!hasMedia && s.Libraries is { Count: > 0 })
        {
            var withPath = s.Libraries.Where(l => !string.IsNullOrWhiteSpace(l.Path)).ToList();
            var pick = withPath.FirstOrDefault(l => l.Label.Equals("Movies", StringComparison.OrdinalIgnoreCase)) ?? withPath.FirstOrDefault();
            if (pick != null) s.MediaFolder = pick.Path.Trim();
        }
        s.Libraries = null;
    }

    public static Settings Clone(Settings s)
    {
        var c = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions) ?? new Settings();
        c.Normalize();
        return c;
    }

    public static void Save(Settings s, string path)
    {
        s.Normalize();
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        s.SavedOnMachine = Environment.MachineName;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions));
        File.Move(tmp, path, true);
    }
}
