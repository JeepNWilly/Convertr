// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.ComponentModel;
using System.Globalization;
using Convertr.Core;

namespace Convertr;

/// <summary>One grid row: the scan entry, its plan, and display-friendly (and sortable) values.</summary>
public sealed class PlanRow
{
    [Browsable(false)] public ScanEntry Entry { get; }
    [Browsable(false)] public PlanItem Plan { get; }

    public PlanRow(ScanEntry entry, PlanItem plan)
    {
        Entry = entry;
        Plan = plan;
        var p = entry.Probe;
        var v = p.Video;
        Library = entry.Library;
        File = entry.RelativePath;
        Container = p.ProbeError != null && p.FormatName == null
            ? entry.Extension
            : Planner.FamilyOf(p, entry.Extension) switch
            {
                Planner.ContainerFamily.Mkv => "mkv",
                Planner.ContainerFamily.Mp4 => "mp4",
                Planner.ContainerFamily.Avi => "avi",
                Planner.ContainerFamily.Asf => "wmv",
                Planner.ContainerFamily.Mpeg => "mpg",
                _ => entry.Extension,
            };
        VideoCodec = v == null ? "?" : v.Codec + (v.BitDepth > 8 ? " " + v.BitDepth + "-bit" : "");
        Resolution = v == null ? "?" : ResolutionClassifier.Label(plan.Resolution);
        VideoKbps = v?.Kbps is long k ? (int)k : null;
        KbpsEstimated = v?.KbpsSource == "estimated";
        Audio = p.Audio.Count == 0 ? (p.ProbeError != null ? "?" : "none") : string.Join("; ", p.Audio.Select(a => a.Describe()));
        Metadata = plan.WritesFile ? plan.Metadata.Summary : "-";
        Action = plan.ActionLabel;
        Reason = plan.Reason;
        SizeBytes = entry.SizeBytes;
        EstSaveMidBytes = plan.EstSaveMidBytes;
        EstSavings = plan.Action == PlanAction.ReencodeVideo
            ? plan.Kind == ReencodeKind.Compat
                ? "compat"
                : plan.LikelyRefused
                    ? (plan.EstSaveHighBytes > 0 ? "likely refused (best case " + Fmt(plan.EstSaveHighBytes) + ")" : "likely refused")
                    : "~" + Fmt(plan.EstSaveLowBytes) + " - " + Fmt(plan.EstSaveHighBytes)
            : "-";
    }

    public string Library { get; }
    /// <summary>Live state during and after a run (queued, 42% 2.1x, done, failed...). Empty before a run.</summary>
    public string Status { get; set; } = "";
    [Browsable(false)] public JobState? JobState { get; set; }
    public string File { get; }
    public string Container { get; }
    public string VideoCodec { get; }
    public string Resolution { get; }
    public int? VideoKbps { get; }
    [Browsable(false)] public bool KbpsEstimated { get; }
    public string Audio { get; }
    public string Metadata { get; }
    public string Action { get; }
    public string Reason { get; }
    public long SizeBytes { get; }
    public long EstSaveMidBytes { get; }
    public string EstSavings { get; }

    public string KbpsText => VideoKbps is int k ? k.ToString("N0", CultureInfo.InvariantCulture) + (KbpsEstimated ? "*" : "") : "";
    public string SizeText => SizeBytes >= 1073741824 ? (SizeBytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : (SizeBytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Sort key so "Re-encode" rows can be grouped: kind first, then action.</summary>
    public string ActionKey => Plan.Action == PlanAction.ReencodeVideo ? (Plan.Kind == ReencodeKind.Compat ? "Re-encode video (compat)" : "Re-encode video (size)") : Action;

    private static string Fmt(long bytes)
    {
        var gb = bytes / 1073741824.0;
        return gb >= 1 ? gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB" : (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    public string Details(Settings? settings = null, string ffmpegExe = "ffmpeg")
    {
        settings ??= new Settings();
        var p = Entry.Probe;
        var v = p.Video;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("File:       " + Entry.FullPath);
        sb.AppendLine("Size:       " + SizeText + "   (" + Entry.SizeBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes)");
        sb.AppendLine("Container:  " + (p.FormatName ?? "?") + (p.DurationSec is double d ? "   duration " + TimeSpan.FromSeconds(d).ToString(@"h\:mm\:ss") : ""));
        if (p.OverallKbps is long ob) sb.AppendLine("Overall:    " + ob.ToString("N0", CultureInfo.InvariantCulture) + " kbps");
        if (v != null)
        {
            sb.AppendLine($"Video:      {v.Codec} {v.Profile}  {v.Width}x{v.Height} ({Resolution})  {v.PixFmt}  {v.BitDepth}-bit  {v.Fps?.ToString(CultureInfo.InvariantCulture)} fps");
            sb.AppendLine($"            bitrate {(v.Kbps?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown")} kbps (source: {(v.KbpsSource == "" ? "unknown" : v.KbpsSource)})"
                + (v.Hdr != "" ? "  HDR: " + v.Hdr : "") + (v.Interlaced ? "  interlaced (" + v.FieldOrder + ")" : ""));
        }
        else sb.AppendLine("Video:      none");
        foreach (var a in p.Audio)
            sb.AppendLine($"Audio:      {a.Language} {a.Describe()}{(a.Profile != null ? " (" + a.Profile + ")" : "")}{(a.IsDefault ? " [default]" : "")}{(string.IsNullOrEmpty(a.Title) ? "" : "  \"" + a.Title + "\"")}");
        foreach (var s in p.Subs)
            sb.AppendLine($"Subtitle:   {s.Language} {s.Codec}{(s.IsImage ? " [image]" : "")}{(s.Forced ? " [forced]" : "")}");
        sb.AppendLine("External subtitle file next to the video: " + (Entry.HasExternalSubs ? "yes" : "no"));
        sb.AppendLine($"Extras:     {p.AttachmentCount} attachments, {p.AttachedPicCount} cover art, {p.DataStreamCount} data streams, {p.ChapterCount} chapters");
        sb.AppendLine("Metadata:   " + Plan.Metadata.Summary + (Plan.WritesFile ? "" : "   (file is not rewritten, so nothing would be removed)"));
        if (!string.IsNullOrEmpty(p.Warnings)) sb.AppendLine("Warnings:   " + p.Warnings);
        sb.AppendLine();
        sb.AppendLine($"Plan:       {Plan.ActionLabel}   (decision-table rule {Plan.Rule})");
        sb.AppendLine("Reason:     " + Reason);
        if (Plan.Action == PlanAction.ReencodeVideo && Plan.Kind == ReencodeKind.Size)
            sb.AppendLine("Estimate:   " + EstSavings + " (estimate, not a promise; the result is only kept if it is clearly smaller)");
        if (Status.Length > 0) sb.AppendLine("Status:     " + Status);
        sb.AppendLine();
        if (Plan.WritesFile && v != null)
        {
            var cmd = QueueBuilder.Preview(Entry, Plan, settings, out var final, out var part);
            var holding = HoldingArea.HoldingPathFor(settings.MediaFolder, Entry.FullPath, settings);
            sb.AppendLine("ffmpeg command (exactly what Convertr runs for this file; a dry run only logs it):");
            sb.AppendLine(cmd.ToCommandLine(ffmpegExe));
            foreach (var n in cmd.Notes) sb.AppendLine("Note:       " + n);
            if (QueueBuilder.NeedsChapterFile(Plan, settings)) sb.AppendLine("Note:       the chapters.ffmetadata file named above is created next to the temporary file just before the run and deleted afterwards.");
            sb.AppendLine();
            sb.AppendLine("Temporary:  " + part);
            sb.AppendLine("Final name: " + final);
            sb.AppendLine("Original:   " + (settings.Originals == OriginalsHandling.DeleteAfterVerify ? "deleted after the result is verified (your choice in Setup)" : "moved to " + holding));
        }
        else sb.AppendLine("No ffmpeg command: this file is not changed (" + Plan.ActionLabel + ").");
        sb.AppendLine("Nothing is changed until you press Convert.");
        return sb.ToString();
    }
}
