---
title: "Convertr: Design Document"
subtitle: "A standalone program that converts a Plex library for direct play"
author: "JeepNWilly"
date: "16 October 2026 (draft v0.1)"
---

Copyright (C) 2026 JeepNWilly. This document is part of Convertr and is released under the GNU General Public License v3.

Project folder: `C:\Projects\Convertr` (code in `C:\Projects\Convertr\source`). Status: design only. No code has been written, and nothing in the library has been changed.

# 1. Goals and non-goals

**Goals**

1. Every file direct-plays on the four target clients (Smart TV Plex app, Fire Stick Plex app, Plex Web, Plex mobile), so Plex never has to transcode video.
2. Quality comes first, then file size. A file is re-encoded only where there is real space to gain, and the result is kept only if it is clearly smaller.
3. It should be simple to use: a portable WinForms exe, **Scan** (a dry run that changes nothing) then **Convert** then **Undo**, plain-text logs, preserved timestamps, nothing deleted unless the user asks, options in a Setup window, and tables that open with no row selected.
4. Plex naming stays exactly as it is: same base name, same folder, same folder structure. Only the extension may change (for example `.avi` to `.mp4`). The source's modified and created times are copied to the output.
5. Runs can be stopped and resumed. Weeks of runtime on 3,140 files / 2.83 TB has to survive a reboot, a power cut or a full drive.

**Non-goals (for now)**

- Cross-platform support, Linux or Docker. Convertr runs on Windows only.
- AV1. The GTX 1080 Ti cannot encode it, and the target clients cannot all decode it.
- HDR, Dolby Vision or 4K handling. The library has none. Convertr detects these and skips them.
- Subtitle download or OCR. External subtitle files are left alone.
- Renaming, metadata matching, or editing Plex's database.
- Re-encoding files that are already HEVC.

# 2. Starting point: the real library (scan of 16 Oct 2026)

| Fact | Value |
|---|---|
| Files / size | 3,140 files, 2,898 GB. Movies: 473 files / 959 GB. Series: 2,667 files / 1,940 GB |
| Containers | mkv 2,124 · mp4 766 · avi 246 · wmv 2 · mpg 1 |
| Video | H.264 1,856 files / 2,070 GB (71%) · HEVC 1,034 / 700 GB (516 are 10-bit) · MPEG-4 ASP (xvid/divx) 245 · VC-1 2 · MS-MPEG4v3 1 · MPEG-1 1 |
| Resolution | 720p 1,888 · 1080p 881 · 480p 299 · 576p 3 · below 480p 68. No 4K, no HDR |
| First audio track | aac 1,555 · ac3 1,098 · eac3 284 · mp3 163 · dts 35 · mp2 2 · wmapro 2 |
| Subtitles | 1,593 files have embedded subtitles. 39 have image subtitles (PGS/VobSub). 163 have external subtitle files |
| Problems | 1 corrupt file (`Movie_C (2013) 1080p AAC.mp4`, moov atom missing). 29 files probed with minor warnings only |

Caveat: for 1,411 files the video bitrate was **estimated** (overall bitrate minus audio), because the container had no per-stream bitrate. Bitrate thresholds below are therefore approximate. Convertr's scanner should record where each bitrate came from (`stream`, `tag` or `estimated`) and show it.

# 3. Conversion profiles and rules

## 3.1 Target format and why

| Choice | Recommendation | Reasoning |
|---|---|---|
| Container | **MP4** (`+faststart`) by default. MKV available in Setup | Plex Web plays MP4 natively in every browser. With MKV, Plex Web often has to "direct stream" (remux on the fly), and some browsers fall back further. Fire Stick, Smart TV and mobile direct-play both containers. MP4 cannot hold ASS or PGS subtitles, but Convertr strips embedded subtitles anyway, so that limit costs nothing. HEVC in MP4 is tagged `hvc1` for best client compatibility |
| Video codec for re-encodes | **HEVC 8-bit (Main)** until 10-bit is confirmed on all devices, then HEVC 10-bit (Main10) | HEVC saves the most space on Pascal-era hardware. 10-bit compresses slightly better and bands less, and 516 files are already HEVC 10-bit. If those play directly on the Fire Stick and the TV today, switch the default to Main10 (see Open Questions) |
| Video codec for legacy files | **H.264 High, 8-bit** | The 249 legacy files are SD and small (126 GB total). The goal here is compatibility, not size. H.264 plays directly everywhere, including Plex Web in any browser, where HEVC support depends on the browser and the hardware |
| Audio | Keep AAC, AC3, EAC3 and MP3. Convert everything else | See 3.4 |
| Subtitles | Strip all embedded tracks. External `.srt/.sub/.idx/.ass/.ssa` files are never touched | Embedded subtitles are removed; external subtitle files are never changed |

Plex Web caveat (honest): browsers do not decode AC3 or EAC3, so Plex Web will usually transcode the **audio only**. That costs little CPU and is not a video transcode. An optional "add AAC stereo compatibility track" setting (off by default) removes even that, at a cost of about 100 MB per two-hour film.

## 3.2 Decision table (applied per file, first matching rule wins)

| # | Condition (from ffprobe) | Action | Output | Notes |
|---|---|---|---|---|
| 1 | Probe failed, no video stream, or duration missing | **Skip (Error)** | none | For example Movie_C. Shown in red. Never queued |
| 2 | HDR, Dolby Vision or 4K and the user has not opted in (Setup: "Process HDR / Dolby Vision / 4K files", off by default). Interlaced video is **not** a Review case unless "Process interlaced files (deinterlace)" is switched off | **Skip (Review)** | none | None in the library today. This is a guard for future additions. **Interlaced files (decision 2026-10-16):** processed like any other file. When they would be re-encoded (rule 3 or 5) the reason says "interlaced: deinterlace (bwdif) will be applied in conversion" and M2 adds `bwdif` to the filter chain. With the Setup option off they are Review under this rule. The library has 2 (Series_P S04), both above the size threshold |
| 3 | Video is MPEG-4 ASP, MS-MPEG4, VC-1, MPEG-1/2, WMV, or the container is AVI/WMV/MPG | **Re-encode video** (compatibility) | H.264 CRF 20 in MP4, audio per 3.4 | 249 files, about 194 h. Keep-if-smaller does **not** apply, because the output may be slightly larger and that is accepted |
| 4 | HEVC (any bit depth) | **Skip**, or **Fix audio only** if rule 6 applies | none | Already efficient. 1,034 files |
| 5 | H.264 with video bitrate at or above the threshold for its resolution (table 3.3) | **Re-encode video** (size) | HEVC in MP4 | 828 files / 1,238 GB / about 700 h at the default thresholds. Keep-if-smaller applies |
| 6 | Video can be kept, but no audio track is AAC/AC3/EAC3/MP3 (DTS, mp2, wmapro, TrueHD, FLAC, PCM, Opus) | **Fix audio only** (video copied as is) | Same container, or MP4 | 35 DTS files on H.264, for example `Movie_E.2016.720p.mkv` |
| 7 | Video and audio can be kept, but the container is not MP4/MKV, or **Clean metadata on skipped files too** is on and junk was found | **Remux only** | MP4/MKV | No re-encode. Nothing in the library needs this for container reasons today |
| 8 | Anything else (efficient H.264 below the threshold, compatible audio) | **Skip** | none | Untouched, including embedded subtitles and metadata |

When a file is processed under rules 3 and 5 to 7, the same pass also strips embedded subtitles (3.5) and cleans junk metadata (3.6). A **Skip** file is never opened for writing. This means its embedded subtitles also stay. That is deliberate: rewriting 2,000 files only to drop subtitles is all risk and no space saving. A separate setting, *Strip subs on skipped files (remux)*, is available and off by default.

## 3.3 Thresholds, CRF and encoder settings (starting values, tuned in M2)

| Resolution (height) | Re-encode H.264 if video ≥ | Files / GB at that threshold | x265 CRF (CPU) | NVENC HEVC CQ (GPU) | x264 CRF (legacy) |
|---|---|---|---|---|---|
| 1080p (≥ 900) | 4,000 kbps | 189 / 514 GB | 21 | 27 | 19 |
| 720p (≥ 650) | 2,500 kbps | 623 / 700 GB | 21 | 27 | 19 |
| 576p/480p | 1,500 kbps | 16 / 24 GB | 20 | 26 | 20 |
| < 480p | 1,200 kbps | 0 | 20 | 23 | 20 |

- **CPU mode:** `libx265 -preset slow -crf N` (or `medium` as a speed option), `-pix_fmt yuv420p` or `yuv420p10le`, `-x265-params aq-mode=3`. Legacy files use `libx264 -preset slow -crf N -profile:v high -level 4.1`.
- **GPU mode (Pascal):** `hevc_nvenc -preset p7 -tune hq -rc vbr -cq N -b:v 0 -spatial-aq 1 -rc-lookahead 32`, with `-profile:v main` or `main10`. Pascal's HEVC NVENC has no B-frames, so expect output measured about **2 times larger than x265 at equal VMAF** (see the measured tuning note below). That is why the GPU CQ values are set a little higher, and the keep-if-smaller check matters more. Legacy files use `h264_nvenc -preset p7 -tune hq -cq 21`.
- Encoder option names must be checked against ffmpeg 9.0.2 (`ffmpeg -h encoder=hevc_nvenc`) during M2.
- Frame rate, resolution, SAR/DAR are kept (no scaling, no frame-rate change). Odd rates such as 23.81 are kept as they are. MPG/WMV sources are checked with `idet` and deinterlaced (`bwdif`) only if they really are interlaced.
- **10-bit:** an 8-bit source can be encoded to Main10 if the user enables it. Output is never forced back from 10-bit to 8-bit, because HEVC 10-bit sources are skipped anyway.
- **Keep-only-if-smaller:** after a size re-encode, the output is kept only if it is at least **15% smaller and at least 100 MB smaller** (both adjustable). Otherwise the temp file is deleted (it is Convertr's own temp file, never user data), the original stays, and the file is marked *Not worth it* so it is skipped in future scans.
- **Projected savings** are per-file estimates from `SavingsModel` (since 0.2.2): output bitrate = A x (source bitrate per 720p-sized frame)^B x pixels x quality scale, fitted on 20 measured clips per encoder (CPU CRF 21: A 31.3, B 0.482; GPU CQ 27: A 97.8, B 0.387; each 3 CRF steps x0.61, 3 CQ steps x0.64), with a spread of x0.71-1.42 (CPU) and x0.81-1.23 (GPU). Bounds that fail the keep rule count as 0, and a zero expected value is shown as likely refused. At default thresholds the real library (670 candidates, 1,179 GB) gives about **373-713 GB, likely 575 GB (CPU)** or **186-496 GB, likely 355 GB (GPU, 68 files likely refused)**. See `docs/RESULTS_SAVINGS.md`. These are estimates, not promises.

## 3.4 Audio rules

| Source audio | Action | Detail |
|---|---|---|
| AAC, AC3, EAC3 (including Atmos-in-EAC3), MP3 | **Copy** | No quality loss. EAC3 Atmos metadata is kept as is |
| DTS, DTS-HD, TrueHD, FLAC, PCM | Convert to **AC3 640 kbps** if the source has more than 2 channels, otherwise **AAC 192 kbps** | 7.1 sources (8 files today) are downmixed to 5.1 for AC3 using ffmpeg's standard matrix |
| mp2, wmapro, Vorbis, Opus, other | Convert to **AAC** (2 ch: 192 kbps, 5.1: 384 kbps) or AC3 640 kbps for 5.1 | wmapro 6-channel (Series_C S03E08/E09) becomes AC3 5.1 |
| Several audio tracks | Keep all tracks, each handled by the rules above, in their original order | The track flagged as default stays default. Language tags are kept |
| Optional | "Keep original audio as a second track" (off), "Add AAC 2.0 compatibility track" (off), "Keep only these languages" (off; for example eng + und) | |

Channel layout: if a source reports `unknown` or an odd layout, it is mapped to the standard layout for its channel count (1=mono, 2=stereo, 6=5.1, 8=7.1). The planner flags the file as *Review* if the count is 3, 4, 5 or 7.

## 3.5 Subtitles

- Default: map no subtitle streams (`-sn`). External subtitle files next to the video are never renamed, moved or touched. If the extension changes (`.avi` to `.mp4`), Plex still matches external subtitles by base name, which stays the same.
- Safety option (**off** by default, so all subtitles are stripped): *Keep embedded subtitles when no matching external subtitle file exists*. 1,532 files have embedded subtitles and no external subtitle file, so this option makes a real difference. When it is on, text subtitles are kept as `mov_text` in MP4 (styling is lost) or as they are in MKV. Image subtitles (39 files with PGS/VobSub) force MKV output, or are dropped with a warning.

## 3.6 Clean up junk metadata (Setup checkbox, **on** by default)

This only applies to files that Convertr already writes (re-encode, fix audio, remux). Skipped files are untouched unless *Clean metadata on skipped files too (remux only, no re-encode)* is on. That second option is off by default and turns those files into rule-7 remuxes.

| Removed | Kept |
|---|---|
| Global tags such as `comment`, `description`, `synopsis`, `encoder`/`encoded_by`, `URL`/`purl`, `copyright` ad text, release-group or tool tags (for example `YTS`, `UIndex.org`, `HandBrake`, `mkvmerge` statistics tags) | `title`, **only if sensible**: kept when it matches the file's base name or `Title (Year)`. Otherwise cleared, and Plex uses its own metadata |
| Per-stream `title`/`handler_name` junk (for example "Encoded by …", URLs) | Audio and video `language` tags. A sensible audio track title such as "Commentary" can optionally be kept |
| Attachments (fonts, cover JPG/PNG), cover-art / `attached_pic` video streams, data and timecode streams | Stream dispositions: `default`, `forced`. Stream order. Duration |
| Chapter titles that are junk (URLs, ads, empty) are renamed to `Chapter 01`… | Standard chapters (Setup option: *Drop chapters*, off by default) |

FFmpeg approach:

- **Explicit maps only:** `-map 0:V:0` (capital V excludes cover art and attached pictures) and `-map 0:a`. Nothing else is mapped, so attachments (`t`), data (`d`) and subtitles (`s`) are dropped. Add `-dn -sn` for good measure.
- **Metadata:** `-map_metadata -1` drops global, per-stream and chapter metadata. Then the kept values are written back explicitly: `-metadata title=…` (or empty), `-metadata:s:a:N language=xxx`, `-disposition:a:N default`, and so on. Dropping everything and re-adding a whitelist is more predictable than selective copying.
- **Chapters:** `-map_chapters 0` keeps them. When titles need cleaning, Convertr writes a small FFMETADATA chapter file and uses `-map_chapters 1` from that input. `-map_chapters -1` when *Drop chapters* is on.
- **Encoder tag:** the `encoder`/`Lavf` tag ffmpeg writes is suppressed with `-fflags +bitexact` (MP4) or overwritten with an empty value. The exact behaviour is checked in M2 tests.
- The planner shows a **Metadata** column (`clean`, `junk: 3 tags, 1 attachment`) so Scan shows what will be removed before anything happens.

# 4. Risk review

| Risk | Likelihood / impact | Mitigation |
|---|---|---|
| Visible quality loss from re-encoding (generation loss on already-lossy H.264) | Medium / High | Conservative CRF. Only high-bitrate files are touched. M2 benchmark with VMAF (target ≥ 95 on 1080p, ≥ 93 on 720p) plus eyeballing dark scenes. Originals kept by default |
| NVENC (Pascal) worse than x265 per bit | High / Medium | GPU and CPU mode chosen per run. GPU CQ set tighter. Keep-if-smaller rejects poor results. CPU mode is recommended for the large 1080p films |
| Corrupt or odd sources (moov missing, decode warnings) | Known: 1 failed, 29 warnings | Rule 1 skips files that fail to probe. Warnings show a *Review* badge. ffmpeg `-err_detect` errors fail the job, never the queue |
| Power loss or crash mid-encode leaves partial files | Medium / Medium | Write only to `<name>.<ext>.convertr-part` (an extension Plex ignores). The swap is the last step. On start-up, orphaned `.convertr-part` files are listed and removed with consent. Queue state is saved after every step |
| Drive full (outputs accumulate while originals are kept) | High if originals are kept / High | Before each job, require free space ≥ 1.3 × source size + 5 GB. Savings are only **realised after purging originals**, so the summary bar shows "space freed now" and "freed after purge" separately. Pause the queue rather than fail |
| Long runs | Certain | About 700 h of footage among size candidates. GPU: roughly 3-8× realtime, so 90-230 h of encoding. CPU x265 slow: likely 0.5-1.5× realtime, so many weeks. These are estimates; M2 measures them. Resumable queue, quiet hours, pause and resume |
| File locks while Plex scans, analyses or streams | Medium / Low | Reading is a shared open. If the swap fails because a file is locked, retry with backoff (5 × 30 s), then requeue. Optional pause while Plex is streaming. Suggest turning off Plex's "Empty trash automatically after every scan" during big runs, so a briefly missing file does not drop watch history |
| Plex treats the converted file as a new item (watched status, posters) | Low-medium / Medium | Same folder and base name usually keep the item. Test on 3 items in M3 before any bulk run. Recommend Plex's "Scan library" after each batch, not live |
| USB 3 exFAT throughput, no journaling | Low for speed, medium for corruption | Even 1080p bitrates are about 1-2 MB/s, far below USB limits. exFAT has no journal, so always use safe eject, and keep the swap atomic (rename within a folder). Optional local scratch folder for a future NAS (with copy-back verification) |
| Timestamps on exFAT (10 ms resolution, time-zone fields) | Low | Copy times with .NET `File.SetLastWriteTimeUtc`/`SetCreationTimeUtc` after the swap. Verify within 2 s |
| Junk-metadata cleaning removes something wanted (good title, commentary label) | Low / Low | Whitelist approach, shown in Scan. Originals kept. Open question on what counts as junk |
| Anti-virus flags (ps2exe exes in particular) | Medium if PowerShell is chosen | Another reason for the C# recommendation (see section 7) |

# 5. Safety model

1. **Scan changes nothing.** It probes, plans and writes `plan.csv`. No temp files, no renames.
2. **Write to a temp file in the same folder:** `Movie (Year).mp4.convertr-part`, with `-f mp4` forced. Same folder means the final swap is a rename on the same volume, not a copy.
3. **Verify before swapping.** Every check must pass:
   - ffprobe opens the output and its duration is within **max(1 s, 0.5%)** of the source.
   - It has exactly 1 video stream (the expected codec and resolution), the expected number of audio streams with language tags, no subtitle streams (unless kept), and no attachment or data streams.
   - The size is sane: at least 5% of the source, and no more than 105% of the source for size jobs (150% for compatibility jobs). The keep-if-smaller rule (3.3) is applied.
   - A quick decode test of the first, middle and last 30 s (`ffmpeg -v error -f null`) produces no errors. A full decode is optional.
   - When cleaning is on, the format tags contain only whitelisted keys, and dispositions match the plan.
4. **Swap:** move the original to the **Originals holding area**, then rename the temp file to the final name, then copy the source's timestamps onto it. The holding area is on the same drive but **outside the Plex library roots** (so Plex never shows duplicates), and mirrors the folder structure: `D:\Media\_Convertr_Originals\Series\Show\S01\…`. If the extension is unchanged, the original's slot is freed first. Each step is logged before and after it happens.
5. **Originals:** kept by default. Alternatives in Setup are *rename in place* (`.convertr-orig`, which Plex ignores), or *Delete original after successful verification*. The delete option needs an explicit confirmation each run and logs every deletion. A separate **Purge Originals…** dialog lists verified originals older than N days, with sizes, and deletes only what the user ticks.
6. **Resumable queue:** `queue.json` records each job's state (`Pending → Encoding → Verifying → Swapping → Done/Failed/NotWorthIt`). On restart, jobs in `Encoding` are restarted, and jobs in `Swapping` are finished or rolled back using the undo journal.
7. **Undo** (per run, per file, or everything): move the converted file to `_Convertr_Undone`, move the original back, and restore its timestamps. Undo never deletes anything. If the original was purged, the row shows *Cannot undo (original purged)*.

# 6. GUI design (WinForms)

## 6.1 Main window

```
+-- Convertr v1.0 ------------------------------------------------------------[_][#][X]-+
| Media: [D:\Media                                                     ] [Browse...] |
| Mode: (o) GPU (NVENC, fast)  ( ) CPU (x265, smaller)    Profile: [Balanced v] [Setup..]|
| [ Scan ]  [ Convert ]  [ Pause ]  [ Stop ]  [ Undo... ]  [ Purge Originals... ]       |
|---------------------------------------------------------------------------------------|
| Filter: Action [All v]  Res [All v]  Search [__________]  [ ] Warnings only          |
|---------------------------------------------------------------------------------------|
| [x]| File                       |Cont|Video          |Audio         |Meta  |Action     |Est.save|Status   |
| [x]| Movie_F.2017.1080p         |mkv |h264 1080 11.1M|ac3 5.1       |junk 2|Re-encode  | 4.0 GB | Pending |
| [x]| Movie_E.2016.720p          |mkv |h264 720  3.9M |dts 5.1       |clean |Re-enc+aud | 1.2 GB | Pending |
| [x]| Movie_G.2012.avi           |avi |xvid 480  1.7M |ac3 5.1       |clean |Re-encode* | compat | Pending |
| [ ]| Series_R.S02E01 (hevc)     |mkv |hevc10 1080    |eac3 5.1      |  -   |Skip       |   -    |         |
| [ ]| Movie_C (2013) 1080p       |mp4 |  ?            |  ?           |  -   |Error      |   -    | moov... |
|---------------------------------------------------------------------------------------|
| Current: Movie_F.2017.1080p.mkv         [#########-------]  58%  212 fps  ETA 0:07:40 |
| Overall: 14 / 829 done      [#-----------------]   2%   Elapsed 1:02   ETA ~4d 6h    |
|---------------------------------------------------------------------------------------|
| 3,140 scanned | Re-encode 828 | Compat 249 | Audio 35 | Skip 2,027 | Error 1           |
| Projected saving: ~250-430 GB (estimate)   Freed so far: 38 GB   After purge: 52 GB   |
+---------------------------------------------------------------------------------------+
```
`*` = compatibility conversion, so the size check does not apply. Rows open with **nothing selected**. A checkbox lets the user exclude individual files. Double-click shows the ffprobe details and the exact ffmpeg command that would run. Right-click offers *Mark always skip*. The status bar log path opens in Notepad.

## 6.2 Setup window

```
+-- Setup ----------------------------------------------------------+
| Profile:   (o) Balanced  ( ) Max quality  ( ) Max savings ( ) Custom|
| Encoder:   (o) GPU NVENC  ( ) CPU x265    x265 preset [slow  v]    |
| Quality:   Smaller [------|----] Better   CRF/CQ 21 / 24 (per res.)|
| Bit depth: (o) 8-bit Main  ( ) 10-bit Main10                       |
| Thresholds (kbps): 1080p [4000] 720p [2500] SD [1500]              |
| Keep only if saving >= [15]% and >= [100] MB                       |
| Container: (o) MP4 (+faststart)  ( ) MKV                           |
| Audio:  [x] Copy AAC/AC3/EAC3/MP3   DTS/other -> [AC3 640k v]      |
|         [ ] Keep original as 2nd track  [ ] Add AAC 2.0 track      |
| Subs:   (o) Strip all embedded                                     |
|         [ ] Keep embedded if no external subtitle file             |
|         [ ] Strip subs on skipped files too (remux)                |
| Metadata: [x] Clean up junk metadata                               |
|         [ ] Clean metadata on skipped files too (remux only)       |
|         [ ] Drop chapters  [Edit junk patterns...]                 |
| Originals: (o) Move to _Convertr_Originals  ( ) Rename in place    |
|            ( ) Delete after verification (asks every run)          |
| FFmpeg:  [C:\Tools\ffmpeg\bin                                 ]  |
|          [Browse] [Detect] [Download...]   ffmpeg 9.0.2  NVENC: OK |
| Parallel jobs: GPU [1 v]  CPU [1 v]   Process priority [Below v]   |
| Quiet hours: [ ] only run 01:00 - 15:00                            |
| [ ] Pause while Plex is streaming  (URL, token stored encrypted)   |
|                                          [ Save ]   [ Cancel ]     |
+--------------------------------------------------------------------+
```
**Media folder (decision 2026-10-16):** the main window has one **Media** box with a Browse button, not separate Movies and Series rows. Libraries are scanned one group at a time; the folder is scanned recursively and remembered in `settings.json` (an older file with a Movies/Series list migrates to the Movies path). The probe cache keeps the other group's entries, so switching between Movies and Series stays fast. The table has no Library column and no Library filter.

**Theme (decision 2026-10-16):** Setup has an *Appearance* group with **Theme: System / Light / Dark**, System (follow the Windows app-mode setting) by default. It applies at once to the main window, Setup, the details dialog, all tables, buttons and the title bar, and System follows Windows live.

FFmpeg lookup order: the folder next to the exe, then `PATH`, then the path saved in Setup, then a *Download…* option (the official gyan.dev essentials build, with checksum shown and user confirmation). *Detect* runs `ffmpeg -hide_banner -encoders` to confirm that `hevc_nvenc` and `libx265` are present.

## 6.3 Oddities panel (a tab beside the results)

This tab lists files that need a human decision instead of a rule: probe errors (Movie_C), decode warnings (29), image-subtitle files when the keep-subs option is on (39), 3/4/5/7-channel audio, and **possible duplicates** (the same title found in two places or with two extensions, for example `Movie.mkv` and `Movie.mp4` in one folder, or a leftover `.convertr-part`). Convertr only reports duplicates. The user decides what to do with them.

# 7. Architecture

## 7.1 Technology choice

| | PowerShell 5.1 + WinForms + ps2exe | **C# .NET 10 (LTS) WinForms** (recommended) | C# WPF |
|---|---|---|---|
| Long-running ffmpeg, live progress, pause/resume | Possible with runspaces and timers, but fragile. UI freezes are a common problem | `async`/`Process` events, `CancellationToken`. Straightforward | Same as WinForms |
| Testability | Pester works, but GUI and logic tend to tangle | xUnit tests on a planner with no UI dependency | Same |
| Distribution | Small exe. ps2exe exes are often flagged by anti-virus | Single-file exe: about 1-3 MB framework-dependent, or about 70 MB self-contained (portable, no install) | Same as WinForms |
| Learning curve | Lowest | Moderate | Highest |

**Recommendation: C# .NET 10 WinForms, published as a self-contained single-file portable exe.** The core of Convertr is a long-running, resumable, multi-process job queue. That is exactly where PowerShell 5.1 is weakest, and where a wrong move costs real data. WinForms gives a native Windows look and feel. If staying in one language matters more, PowerShell is workable for M1 (the scanner), but the worker should still be designed as described below.

## 7.2 Modules

| Module | Responsibility |
|---|---|
| `FfmpegLocator` | Finds ffmpeg/ffprobe, checks the version and NVENC support |
| `Scanner` | Walks the library folders and runs `ffprobe -show_format -show_streams -show_chapters -of json`, cached by path, size and mtime. Read-only |
| `Planner` (rules engine) | A pure function from probe result and settings to a `PlanItem`: action, reasons, ffmpeg arguments, estimated savings, metadata findings. No disk I/O |
| `QueueWorker` | Runs jobs (1 by default), parses `-progress pipe:1` for fps and ETA, handles pause (suspend or requeue), stop, quiet hours and the Plex-streaming check |
| `Verifier` | The checks in section 5.3, returning pass or fail with reasons |
| `Swapper` + `UndoJournal` | Atomic moves, timestamp copy, journal written ahead of each step, Undo and Purge |
| `Logger` | Plain-text log |
| `Settings` | Load and save `settings.json`. The Plex token is protected with DPAPI |
| `MainForm`, `SetupForm`, `OdditiesTab` | UI only, bound to the components above |

## 7.3 Data files (all next to the exe in a `Convertr-data` folder, in keeping with portability)

| File | Content |
|---|---|
| `settings.json` | Every Setup option, the ffmpeg path, thresholds, junk patterns |
| `plan_YYYYMMDD_HHMM.csv` | One row per file: Library, RelativePath, Container, Video, Audio, MetaFindings, Action, Reason, EstSaveMB, FfmpegArgs |
| `queue.json` | Job states for resume |
| `undo_YYYYMMDD_HHMM.csv` | RunId, Time, OriginalPath, HoldingPath, NewPath, OrigSize, NewSize, OrigMtimeUtc, OrigCtimeUtc, Step, Status |
| `logs\Convertr_YYYYMMDD_HHMM.log` | Readable log, plus each ffmpeg stderr tail on failure |
| `probe-cache.json` | Speeds up rescans |

## 7.4 Conventions

Convertr follows these conventions: Scan, Full Run and Undo vocabulary; dry run by default; plain-text logs and CSV journals; preserved timestamps; no deletion without an explicit choice; options in Setup; tables with no initial selection; portable exe with no installer; GPL v3 headers (`Copyright (C) 2026 JeepNWilly`) on every source file; README, docs and LICENSE in the same style on GitHub under **JeepNWilly**.

# 8. Build, test and release plan

| Milestone | Scope | Exit criteria |
|---|---|---|
| **M1** Scanner and preview | Locator, Scanner, Planner, Main window table, filters, summary bar, plan CSV | Scan of the real library reproduces the counts in section 2. Plan numbers match `library_formats.csv`. Zero writes to Z: (verified with a before/after directory listing) |
| **M2** Converter on test files | QueueWorker, ffmpeg arguments, GPU and CPU modes, metadata cleaner. **Benchmark** | 10 real files copied to a scratch folder (not converted in place). Measured size ratio, fps and VMAF/SSIM for each mode and CRF. Thresholds and CRF retuned |
| **M3** Safety | Verifier, Swapper, UndoJournal, resume, free-space guard, lock retry, Purge dialog | Fault-injection tests pass. 3 real Plex items converted and checked on all 4 devices (direct play shown in the Plex dashboard, watched status kept) |
| **M4** GUI polish | Setup window, Oddities tab, ETA, quiet hours, optional Plex-streaming pause, About/licence | Usable end to end on a 50-file batch |
| **M5** Release | README/docs, screenshots, `Convertr-v1.0.0-win-x64.zip` attached as a GitHub Release asset, SHA-256 in the release notes | A clean Windows VM runs the exe from a USB stick |

**Test plan**

- **Unit (Planner):** mocked ffprobe JSON fixtures (no real media): H.264 1080p at 11 Mbps (re-encode), H.264 720p at 1.2 Mbps (skip), HEVC 10-bit (skip), xvid AVI (compat), DTS 5.1 (fix audio), wmapro 6ch, 7.1 TrueHD, no-audio or moov-missing (error), cover-art stream plus font attachments plus a `comment=Downloaded from …` tag (cleaned), and an MKV with PGS when keep-subs is on. One expected `PlanItem` per fixture.
- **Integration:** tiny synthetic clips (10 s, generated with `ffmpeg -f lavfi testsrc`) covering every container and codec pair, plus external `.srt` files. Assertions: base name unchanged, external subtitle files untouched, mtime copied, no subtitle, attachment or data streams, only whitelisted tags, language and default flags kept, chapters kept or dropped as configured.
- **Fault injection:** kill ffmpeg mid-encode, cut power (kill the process during the swap), fill a small VHD to 100%, hold an exclusive lock on the source, truncate the output. Expected result: the original is intact, the queue resumes, and the log explains what happened.
- **Benchmark before any bulk run:** about 8 representative files (for example `Movie_F.2017.1080p.mkv` at 11 Mbps, `Movie_H.2018.1080p.mkv` at 4.5 Mbps, `Movie_I 2011 720p.mkv` at 3.0 Mbps, a 1080p drama episode, a 720p sitcom episode, a dark film, an xvid AVI). Encode with GPU CQ 22/24/26 and CPU CRF 20/21/22. Record size, time, VMAF (`libvmaf`, if the ffmpeg build includes it; otherwise SSIM/PSNR) and a 2-minute visual check on the TV. Pick defaults from the results and update section 3.3.
- **Rollout:** run 20 files, then 100, then by library, starting with **Movies 1080p over 8 Mbps** (62 files, 168 GB) where the gain per file is largest.

# 9. Open questions (recommended default in bold)

**Decision (2026-10-16): the recommended default is accepted for every question below, and each one is exposed as a changeable option in the Setup window.**

1. Does existing HEVC 10-bit play directly on the Fire Stick and the TV (check Plex dashboard, "Direct Play")? Which Fire Stick model? **Use 8-bit until confirmed, then Main10.**
2. GPU or CPU as the usual mode? **GPU for Series 720p, CPU for 1080p Movies over 8 Mbps.**
3. MP4 or MKV as output? **Decided: an "Output container" dropdown in Setup (MP4 or MKV), MP4 selected by default.**
4. Re-encode thresholds (1080p ≥ 4,000 / 720p ≥ 2,500 kbps)? **Keep them, retune after M2.**
5. Minimum saving to keep an output? **15% and 100 MB.**
6. Originals handling? **Move to `_Convertr_Originals`, purge manually after about 2 weeks of spot checks.**
7. How much free space does Z: have? It limits batch size while originals are kept. **Batches of 200 GB or less until known.**
8. DTS conversion: replace, or keep the original as a second track? **Replace with AC3 640k.**
9. Add an AAC stereo track for Plex Web? **No.**
10. What counts as junk metadata? **Global comment/description/encoder/URL/release-group/tool tags, attachments, cover art, data streams, and junk chapter titles; keep sensible titles, languages, dispositions and chapters.** Should a sensible embedded title be kept or always cleared? **Keep only if it matches the file name.** Keep audio track titles like "Commentary"? **Yes.**
11. Clean metadata or strip subtitles on skipped files too? **No.**
12. Pause while Plex is streaming (needs a Plex token) or use quiet hours only? **Quiet hours only in v1.**
13. Implementation language? **C# .NET 10 WinForms.**
14. Interlaced files: Review or process? **Decided 2026-10-16: process them (deinterlace with `bwdif`); a Setup option "Process interlaced files (deinterlace)", on by default, switches back to Review.**
15. One Media folder or two library rows? **Decided 2026-10-16: one "Media" box (scan one group at a time), remembered between runs.**
16. Theme? **Decided 2026-10-16: Setup option System / Light / Dark, System by default.**

*Implementation note: Convertr M1 targets .NET 10 (`net10.0-windows`), matching section 7.1.*

# 10. Milestone 2 as built: deviations and findings (16 Oct 2026)

- **Compat target is H.264, as in 3.1.** Legacy sources (xvid/divx, WMV, MPEG-1/2) are re-encoded to H.264, never to HEVC, and the keep-if-smaller rule only applies to size re-encodes (a compat re-encode of a small AVI is normally *larger* and that is fine).
- **NVENC and the NVIDIA driver (important).** The Windows ffmpeg builds from 8.1 on (including 9.0.2) are compiled against NVENC API 13.1. On this machine (GTX 1080 Ti, driver 581.80, NVENC API 13.0) they cannot open `hevc_nvenc` or `h264_nvenc`, while 8.0.1 works. 8.1.2 and 9.0.2 fail the same way. Options: update the driver, use ffmpeg 8.0.1 for GPU mode, or use CPU mode. Convertr tests this in Setup (`FfmpegLocator.TestNvencAsync`), tells the user, offers "Download older" (a list of older versions with notes on the driver each needs), and the main window offers CPU mode when the encoder cannot start. A failed start is safe: the original is untouched and no temporary file remains.
- **Holding layout.** Originals go to `<parent of Media>\_Convertr_Originals\<Media folder name>\<relative path>` (for example `D:\_Convertr_Originals\Media\Series\...`), a sibling of the Media folder, so a media server rooted at the Media folder never sees them. Undo moves converted files to the matching `_Convertr_Undone` folder. A name that is already taken gets a numeric suffix. A drive-root Media folder uses a folder inside it.
- **Part files.** `Name.mp4.convertr-part` next to the source. A chapter file for cleaned chapters is `Name.mp4.chapters.convertr-part`. Both are deleted on any failure or Stop.
- **Verification.** Duration tolerance is max(1 s, 0.5%); the size sanity window is 5% to 105% of the source (150% for compat re-encodes, 110% for remux and audio fixes); the decode test covers three 20 s slices (start, middle, end; the whole file when it is under 2 minutes) and can be switched off in Setup (Verify decode, default on). A false-positive filter ignores non-monotonic dts and QuickTime chapter-reference messages.
- **Run controls added to the design:** Stop after this file, an optional run window (quiet hours) that pauses the queue outside the window, a batch size limit (default 0 = none) and a resume offer at start-up.
- **Cleaning.** `-map_metadata -1` plus `-fflags +bitexact -flags:v +bitexact -flags:a +bitexact` removes the encoder tag; audio language tags and a sensible title are written back; chapter titles are cleaned through an FFMETADATA input.
- **Download FFmpeg** (Setup): gyan.dev release essentials (SHA-256 verified), then the GitHub mirror of the same builds, then BtbN; only `ffmpeg.exe`, `ffprobe.exe` and the licence text are extracted (no zip-slip); target `ffmpeg\` next to the exe or `%LOCALAPPDATA%\Convertr\ffmpeg`.
- **Dialogs** open centred over the main window (`CenterParent`, `ShowDialog(owner)`) and are clamped to the working area of the monitor that holds the main window (`DialogGeometry.Compute`); taller dialogs are limited in height and scroll.
- Encoder options are verified in `docs/ENCODER_OPTIONS_VERIFIED.txt`.
- **Stale bitrate tags.** A stream or `BPS` tag bitrate that is more than 10% (+100 kbps) above the bitrate of the whole file is ignored and the video bitrate is estimated from the file size (marked `*`). 158 files (59 GB) of the library had such tags and were wrongly size candidates; the real library now has 670 size candidates (Movies 78, Series 592), 249 compat, 4 audio fixes, 2,213 skips. `probe_cache.json` schema 2 forces one re-probe.
- **Measured tuning (M2, VMAF against the source, 90 s clips of 720p H.264).** x265 slow CRF 21: 31-38% of the source, VMAF 96.0-97.2; CRF 24: 18-22%, VMAF 94.7-96.3. NVENC (Pascal) CQ 24: 83-91%, VMAF 96.3-97.4; CQ 27: 51-58%, VMAF 95.4-96.8; CQ 30: 32-37%, VMAF 94.1-95.9. At equal VMAF x265 is about 2x smaller than NVENC on this GPU at equal VMAF (measured again in 0.2.2), far more than the 10-25% once assumed. Recommended: keep CRF 21 (CPU), raise the GPU CQ default from 24 to 27. Full tables: RESULTS.md.
- **Hardware detection and defaults (0.2.1).** On the first launch and when the GPU, driver or ffmpeg version changes (or on demand in Setup) Convertr reads CPU and GPU facts (CIM, nvidia-smi), really starts `hevc_nvenc`/`h264_nvenc` (1 s test, and two sessions together) and chooses defaults from the M2 measurements: CPU x265 by default (about 2 times smaller than NVENC at equal VMAF), GPU only when the CPU has fewer than 4 threads (Auto policy) or the user prefers speed; preset slow with 6 or more threads, else medium; GPU CQ 27; parallel jobs 1 CPU, up to 2 GPU. Settings the user changed by hand are tracked per setting and never overwritten. The speed estimate uses a 20-second benchmark when one exists, else typical speeds (about 1x real time for x265 slow on 4 cores / 8 threads, about 10x for NVENC on a GTX 1080 Ti).
