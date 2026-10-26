# Changelog

Convertr follows [semantic versioning](https://semver.org/). `VERSION` is major.minor.patch; `BUILD_NUMBER` goes up by one on every build.

## 0.3.0 - Platform seams for the Linux service (build 10, rebuilt as build 11)

Internal refactor: **no user-visible change in the Windows program** (same plans, same ffmpeg command lines, same files, same settings.json). Preparation for Convertr_svc, a Linux / web edition that shares this Core.

### Changed (internal)
- New platform layer in Core (`Platform.cs`): `IAppPaths`, `IProcessController` (pause/resume), `IFileTimes`, `IPlatformHardware`, `IPlatformServices` and `PathCompare` (case-insensitive paths on Windows, case-sensitive on Linux). The Windows implementations are the code that used to be inline (ntdll suspend/resume, creation-time handling, CIM + nvidia-smi detection, exe/LocalAppData locations), moved without changes. The Linux implementations (`PlatformLinux.cs`: /proc/cpuinfo, `lspci`, SIGSTOP/SIGCONT, `/var/lib/convertr-svc`) are used only on Linux.
- **ffmpeg environment hook:** every ffmpeg / ffprobe process start goes through `FfmpegEnvironment.Apply`; nothing is applied unless the new `FfmpegEnvironment` setting has entries (needed on Intel Battlemage GPUs on Linux).
- **`IJobStorage`** seam in front of the swap; `InPlaceStorage` wraps the existing `Swapper` unchanged and is what the engine uses by default.
- (build 12) Convertr_svc S3 additions in Core, inactive in the Windows program: Intel VAAPI/QSV/AV1 branches in `FfmpegCommandBuilder` (only for `GpuBackend` Qsv/Vaapi), `ScratchStorage` and default-implemented scratch hooks on `IJobStorage`, `IntelProbe`, `JobState.CopyingIn/CopyingBack`, new settings `GpuDevice`, `IntelCq`, `IntelAv1Cq`, `GpuEncoderArgs`. The golden plan/command output is byte-identical and all Windows tests pass.
- (Convertr_svc 0.3.1, inactive in the Windows program) Intel defaults from the Arc Pro B60 benchmark: `IntelCq` 23 confirmed for VAAPI HEVC, new setting `IntelQsvCq` (21, same quality as VAAPI 23), `IntelAv1Cq` stays an untested placeholder. `SavingsModel` has an Intel estimate (used only when `Mode` is Gpu and `GpuBackend` is Vaapi or Qsv; the NVENC and CPU estimates are unchanged) and `PlannerOptions` carries `GpuBackend`, `GpuCodec`, `IntelCq`, `IntelQsvCq`. Golden output unchanged; no Windows rebuild needed.
- The Windows `AppPaths` helper now delegates to the Core platform paths (same folders as before).
- Build 11: `Scanner` does not enter symbolic links to folders on Linux/macOS (they could leave the media folder or loop). Windows keeps its behaviour (junctions and links are followed as before); golden plan output identical. New test `LinuxScannerTests`.

### Added
- Additive settings (defaults reproduce today's behaviour, old settings.json files load unchanged): `GpuBackend` (Nvenc), `GpuCodec` (Hevc), `FfmpegEnvironment`, `FfmpegEnvironmentAuto`, and the scratch-disk options `UseScratch` (off), `ScratchFolder`, `ScratchBudgetGb`, `MaxScratchPerJobGb`, `MaxConcurrentTransfers`, `CopyBackVerify`, `StopEncodeEarly`, `KeepScratchOnFailure`, `NasOfflineRetryHours`, `StaleScratchHours`. None of them has a Setup screen yet.
- 11 tests (377 in total): platform conventions, Linux parsers (/proc/cpuinfo, `lspci` with an Arc Pro B60), ffmpeg environment, Linux pause/resume of a real process, settings compatibility, `InPlaceStorage`, and a golden test that replays the real library scan through the planner, the savings model and the command builder (set `CONVERTR_GOLDEN_OUT` to write the dump for before/after comparison).
- One test assumption made platform-aware: probe-cache paths are case-insensitive only where the platform's paths are.

## 0.2.4 - Choose an older FFmpeg (build 9)

### Changed
- The Setup button **Download older (8.0.1)** is now **Download older** and opens a dialog with a list of older ffmpeg versions. The dialog opens centered over Setup, can be resized, follows the theme, starts with nothing selected, and **Download** stays disabled until a version is picked (double-click also downloads). **Download FFmpeg** (latest) works as before.
- The list is read online from the releases of the official gyan.dev builds on GitHub (`GyanD/codexffmpeg`, essentials builds only, numbered releases only, without the newest one). If the lookup fails (offline, rate limit) a built-in list is shown: 8.0.1, 8.0, 7.1.1, 7.0.2 and 6.1.1 (each URL was checked).
- Every row has a note and a "With your GPU" column. The note says which NVIDIA driver NVENC needs with that version (for example 8.0.x: NVENC API 13.0, driver 570 or newer, works on GeForce 10 series; 8.1 and newer: driver 610 or newer). When a hardware profile exists, the row says whether it is known to work with your detected NVIDIA driver.
- The chosen version goes into a folder of its own, `ffmpeg\<version>` (next to the exe, or under `%LOCALAPPDATA%\Convertr\ffmpeg` when that is not writable), so versions never overwrite each other, and the FFmpeg folder in Setup is set to it. A version that is already downloaded is just selected again. Downloads are checked by size and, when the release list publishes one, by SHA-256.

### Added
- `FfmpegVersions` (release list parser, fallback list, notes, driver table), 22 tests (366 in total), harness command `versionshots`.

## 0.2.3 - Resizable Setup window and no machine-specific defaults (build 8)

### Changed
- **The Setup window can be resized and maximized.** Text and path boxes stretch with the width, wrapped text re-wraps, the content scrolls (sideways and up and down) when the window is small, and Save and Cancel stay pinned at the bottom right. It still opens centered over the main window and fully on screen. The size you save is remembered (`SetupWidth` / `SetupHeight` in settings.json) and is clamped to the screen on the next opening. Minimum size 560 x 420.
- **A fresh Convertr has no built-in paths.** Cause of a stale path on a second computer: the program itself contained default paths (a Media folder on the author's NAS as the starting value, and an ffmpeg folder under the author's user profile as the last place to look for ffmpeg), so a copy of the exe without settings.json still showed them. Both are removed: the Media field starts empty (with the hint "Choose your Media folder (Browse...)"), and ffmpeg is looked for next to the exe, in its `ffmpeg` subfolder, in Convertr's download folder and on PATH, otherwise the "ffmpeg not found" message appears.
- **Saved settings are checked against the computer at start-up** (`SettingsValidator`). A Media folder that does not exist is cleared when the settings were saved by another computer (or by an older version that did not record the computer name); on the same computer it is kept, because a network drive can simply be offline. An FFmpeg folder without ffprobe is cleared on any computer. A hardware profile from another computer (different computer name, or a different processor for older profiles) is detected again, and the benchmark speeds measured elsewhere are cleared. Nothing is deleted: only fields are cleared and the file is rewritten; a notice at the top explains what happened.
- settings.json records the name of the computer that saved it (`SavedOnMachine`); the hardware profile records `MachineName`.

### Added
- Tests for the size rules, the saved size, the start-up validation and the empty defaults (344 in total); harness commands `setupshots` (Setup at default, large, small and minimum sizes in Light and Dark, with layout checks) and `launch --foreign true` (simulates settings from another computer).

## 0.2.2 - Recalibrated projected savings (build 7)

### Changed
- **Projected savings are now per file instead of flat percentages** (`SavingsModel`). The expected output bitrate depends on the source bitrate and resolution (fit on 20 measured library clips per encoder), so 720p files at 3-4.5 Mbps show about 50% (CPU) or 33% (GPU) and 1080p files at 4.5-6 Mbps about 42% (CPU) or 12% (GPU). The old flat 30-40% (CPU) and 20-35% (GPU) of the file are gone; the four Save Low/High settings were removed.
- **Keep-if-smaller is predicted.** Each estimate (optimistic, expected, pessimistic) that would fail the 15% / 100 MB keep rule counts as 0, and a file whose expected saving is 0 is marked "likely refused (best case ...)" in the table; the summary and the run confirmation show the range, the most likely value and the number of likely refused files.
- Library estimate (3,136 files, 670 candidates, 1,179 GB): CPU 373 / 575 / 713 GB (low / likely / high), GPU 186 / 355 / 496 GB with 68 files (102 GB) likely refused. Details and data: `docs/RESULTS_SAVINGS.md`.
- **Correction:** x265 is about 2 times smaller than NVENC at equal VMAF, not 2.5 times (the Auto explanation text, the user guide and the tests were updated).
- The NVENC problem text now says that ffmpeg 8.1 and newer need driver 610 or newer and that GeForce 10 series cards cannot get it (use ffmpeg 8.0.1 or older, or the CPU).

### Findings
- Driver 582.78 does not enable NVENC with ffmpeg 9.0.2 on the GTX 1080 Ti (needs NVENC API 13.1, driver gives 13.0). Auto stays on CPU.
- NVENC option sweep (Pascal, ffmpeg 8.0.1): `-multipass`, `-aq-strength` and `-temporal-aq` give nothing or are unsupported; the current settings stay, CQ 27 stays the GPU default.

### Added
- `SavingsModelTests` (model monotonicity, keep rule, measured-sample checks), harness command `savings` (per-mode, per-bracket report from the probe cache, no library access), `docs/RESULTS_SAVINGS.md` and the data in `docs/data/`.

## 0.2.1 - Hardware detection and automatic defaults (build 6)

### Added
- **Hardware detection** (`HardwareDetector`): CPU name, physical cores and threads, GPU name, VRAM and driver (CIM/WMI plus `nvidia-smi` when present; the NVIDIA driver is shown as 581.80, not the long Windows number), and a real test of `hevc_nvenc` and `h264_nvenc` with the ffmpeg that Convertr found (a 1-second generated `testsrc` encode to the null output, plus two sessions at once to see if parallel GPU jobs work). Intel QSV and AMD AMF are only reported.
- **Automatic defaults** (`HardwareDefaults`), chosen with the measured M2 results: CPU x265 CRF 21 gives files about 2 times smaller (corrected from 2.5 in 0.2.2) than NVENC, so **Auto** picks the CPU unless the CPU has fewer than 4 threads and NVENC works. x265 preset `slow` with 6 or more threads, `medium` with fewer; 1 CPU job; up to 2 GPU jobs when two NVENC sessions work; GPU quality CQ 27 (was 24: 51-58% of the source instead of 83-91%, VMAF about 96). Policy in Setup: *Auto (recommended)*, *Prefer CPU quality*, *Prefer GPU speed*.
- **Runs in the background** on the first launch (no profile saved) and when you press **Re-detect hardware** in Setup; also automatically when the graphics card, its driver or the ffmpeg version changed since the last run, with a non-blocking message in the status line. It never overwrites a setting you changed by hand: Setup and the Mode radio buttons record "changed by the user" per setting. **Reset to recommended** forgets those and applies the defaults again.
- Setup **Hardware** section: detected CPU, GPU, driver, ffmpeg version, NVENC status ("works" or "not available: reason", for example the driver is too old for this ffmpeg, with the suggestion to use ffmpeg 8.0.1 or update the driver), the chosen defaults, a "Why" line and the buttons. The profile is saved in `settings.json` with a timestamp.
- **Speed estimate** in the run confirmation dialog (hours for the files about to be converted, from typical speeds or from the benchmark) and an optional **Run 20-second benchmark** button in Setup: encodes 20 s of a file from the list with the chosen mode into a scratch file in `%TEMP%` (deleted afterwards), reports the speed as times real time and the estimated time for the whole queue.
- `x265` thread pool setting (`X265Threads`, 0 = automatic; adds `pools=N` only when set).
- Harness command `hardware` (real detection, defaults, optional benchmark and Setup screenshots). 42 new tests (CIM and nvidia-smi sample parsing, decision logic for NVENC ok or not, few or many threads and every policy, user-modified flags preserved, estimates).

### Changed
- The default GPU quality is now CQ 27 (see above). DESIGN.md 3.3 and the golden test were updated.

## 0.2.0 - Milestone 2: the conversion engine (unreleased)

Convertr now converts. Everything is tested on scratch copies; the library is only touched when you press Convert and confirm.

### Added
- **FFmpeg command builder** (`FfmpegCommandBuilder`): one explicit command per plan item, shown in full in the Details dialog (also in dry-run mode).
  - Video: `libx265` (CPU), `hevc_nvenc` (GPU), `libx264` / `h264_nvenc` for legacy sources, or `-c:v copy`. Settings follow DESIGN.md 3.3 (x265 CRF 21, NVENC CQ 24 at the time (27 since 0.2.1), legacy x264 CRF 20; one step better for SD).
  - Interlaced files get `bwdif=mode=send_frame:parity=auto:deint=all` only when flagged; no scaling, no frame-rate change.
  - Audio: AAC/AC3/EAC3/MP3 copied, everything else converted per DESIGN.md 3.4. Subtitles stripped by default, or kept (`mov_text` in MP4, as-is in MKV; image subtitles force MKV).
  - Junk-metadata cleaning with explicit stream maps (`-map 0:V:0`, `-map 0:a:N`, `-map_metadata -1`, `-fflags +bitexact`), a whitelist written back (title, language, default/forced), cleaned chapter titles through an FFMETADATA file, `-movflags +faststart`, `-tag:v hvc1`.
  - All options were checked against ffmpeg 9.0.2 and 8.0.1 (`docs/ENCODER_OPTIONS_VERIFIED.txt`).
- **Queue and worker** (`ConversionEngine`): one job at a time (parallel GPU/CPU jobs are a Setup option, default 1), progress from `-progress pipe:1` (percent, speed, file ETA, overall ETA), **Pause/Resume** (the ffmpeg process is suspended), **Stop** and **Stop after this file**, an optional run window (quiet hours) and batch size limit (0 = none). The queue is saved to `queue.json` after every step and can be resumed after a crash or restart. Free space is checked before each job. Output goes to `Name.mp4.convertr-part` next to the source.
- **Verifier**: ffprobe on the output (duration within max(1 s, 0.5%), expected streams and codecs, language tags, no stray attachment/data streams, size sanity, cleaned tags), a test decode of the start, middle and end (harmless dts and chapter-reference messages are ignored), and the **keep-only-if-smaller** rule (15% and 100 MB, adjustable) for size re-encodes.
- **Swap**: original moved to `_Convertr_Originals` (same folder structure, outside the Media folder), the verified output moved into place with the same base name (new extension if the container changes), original dates copied (modified and created). A failed step rolls the original back. Name clashes are refused. Every step is journaled first.
- **Log and Undo**: plain-text log per run (`Logs\Convertr_*.log`) and an undo CSV per run. **Undo** moves the converted file to `_Convertr_Undone` (never deletes it) and restores the original and its dates; it says when an original is gone.
- **Safety**: dry run (shows the exact commands, changes nothing), a first-run confirmation dialog (counts, projected saving, where originals go), refusal on output clash, no partial files after a failure, clean handling of ffmpeg crashes, Stop and a full disk.
- **Download FFmpeg** button in Setup (next to the ffmpeg folder): downloads the official free Windows essentials build from gyan.dev (fallbacks: the GitHub mirror of the same builds, then BtbN's GitHub build) with a progress bar, checks the SHA-256 where the site publishes one, extracts only `ffmpeg.exe`, `ffprobe.exe` and the licence into an `ffmpeg` folder next to Convertr.exe (falls back to `%LOCALAPPDATA%\Convertr\ffmpeg` when that folder is not writable), sets the path and shows the detected version. Offline or blocked downloads give a clear message. A status line shows "FFmpeg found: version X at path" or "FFmpeg not found" with a hint to press Download.
- **GPU check**: Setup runs a one-second NVENC test and says when the NVIDIA driver is too old for the ffmpeg build (ffmpeg 8.1 and newer, including 9.0.2, need NVENC API 13.1; driver 581.80 offers 13.0). A "Download older (8.0.1)" button fetches a build that works with that driver, and the main window offers CPU mode when the GPU encoder cannot start.
- Main window: Convert, Pause, Stop, Stop after this file and Undo now work; a Status column and per-file progress; a dry-run checkbox; a resume prompt when a saved queue exists.
- Setup: verify-decode option, batch limit 0 = none, no more "coming in M2" notes.
- xunit tests for the builder (golden command lines), verifier (mocked ffprobe JSON), queue persistence, swap, timestamp preservation, undo, the downloader (path detection, zip extraction from a small test zip, zip-slip protection) and the dialog geometry.

### Fixed
- **Stale video bitrate tags.** Some rips (mkvmerge/GalaxyRG releases that were trimmed or re-muxed) carry a `BPS` tag from the original, larger file. When a video bitrate from the stream or tag is higher than the bitrate of the whole file it is now ignored and the bitrate is estimated from the file size (`*`). In the real library 166 H.264 files (62.5 GB) were wrongly counted as size candidates this way; the real test found one (Series_Q S01E06: tag 7,012 kbps, file 912 kbps, CPU re-encode came out 143% of the source and was refused). The probe cache version went up to 2, so the first scan after updating re-probes everything once.
- Undo of a file whose converted name equals the original name (MP4 to MP4) said "original place is taken"; it now works (test added).

### Changed
- **Dialogs open centred over the main window and stay on screen**: Setup, Details, the confirmation dialog, Undo and the text editor use `StartPosition = CenterParent` and `ShowDialog(owner)`, then are clamped to the working area of the monitor that holds the main window (a dialog taller than the screen is limited in height and scrolls). Setup used to open too low because its size was set after it was positioned.
- Setup groups rebalanced (Run schedule moved to the left column) so the window is shorter.

### Notes
- Compatibility re-encodes (legacy sources) target H.264, as in DESIGN.md 3.1; the keep-if-smaller rule applies to size re-encodes only.
- The window title and docs now say version 0.2.0.

## 0.1.0 - Milestone 1: scanner, planner and preview (unreleased)

Scan and preview only. Convertr does not convert anything yet and never writes to the library.

### Added
- Main window: one **Media** folder box with Browse (defaults to the Movies folder, remembered in `settings.json`; an older Movies/Series list migrates to the Movies path), scanned recursively, one group at a time; Scan and Cancel, results grid (file, container, video codec, resolution, video kbps, audio, metadata findings, action, reason, estimated savings), filters (action, resolution, search, warnings only), progress bar and a summary bar with counts per action, total size and a projected saving that is labelled as an estimate. Double-click a row for the full details.
- Tables and dialogs open with no row selected and no text highlighted (`CurrentCell` is cleared on `DataBindingComplete` and `Shown`).
- Convert, Pause, Stop, Undo and Purge Originals buttons exist but are disabled ("coming in M2" / "coming in M3").
- Setup window with every option from the design (DESIGN.md section 9): FFmpeg folder with auto-find and Detect, GPU/CPU mode, output container (MP4 default), bitrate thresholds, keep-if-smaller (15% and 100 MB), subtitle options, junk-metadata options (with an editable pattern list), timestamps, originals handling and the other accepted defaults. Options that only matter once conversion exists are saved and labelled "used when conversion is enabled". Settings are stored in `settings.json` next to the exe.
- Scanner: `ffprobe -v error -print_format json -show_format -show_streams -show_chapters`, 3 probes in parallel, resumable `probe_cache.json` (keyed by path, size and modified time), cancellable, safe with special characters in paths, never modifies a file.
- Planner: the decision table (rules 1 to 8) as pure functions using the thresholds from Setup. Review guard for HDR, Dolby Vision, 4K, unknown codecs, unknown bitrates and ambiguous audio channel layouts.
- Interlaced files are processed, not sent to Review: re-encodes get the note "interlaced: deinterlace (bwdif) will be applied in conversion". Setup option "Process interlaced files (deinterlace)", on by default; off marks them Review again. (The Series_P S04 files are now size candidates: 828, Review 0.)
- Theme option in Setup: System (default, follows Windows live), Light or Dark, applied to the main window, Setup, the details dialog, tables, buttons, scroll bars, tool tips and the title bar.
- The progress, summary and green projected-saving lines wrap onto more lines when the window is made narrower instead of being cut off (the grid above resizes to fit). The mode, button and filter rows wrap too, and the minimum window size is smaller. Setup has no green text; its long labels already wrap.
- Export of the plan to CSV (refuses to write inside the Media folder).
- xunit tests for the planner, the ffprobe parser, settings, cache, scanner and the FFmpeg locator, plus a check against the real library scan.
- `build.ps1` (single-file win-x64 publish to `..\Convertr.exe`), README, docs, GPL v3 license.

### Notes
- Targets .NET 10 (`net10.0-windows`), matching the design. `build.ps1` looks for the .NET 10 SDK on PATH, then in `%LOCALAPPDATA%\Microsoft\dotnet`.
- Settings changes: `MediaFolder` and `Theme` added; `Libraries` is read once for migration and not written; `AllowHdr4kInterlaced` became `AllowHdr4k` and `ProcessInterlaced` was added.
