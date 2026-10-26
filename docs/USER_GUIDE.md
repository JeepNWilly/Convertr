# Convertr user guide (v0.3.0, build 12: scan, preview, convert, hardware defaults)

Version 0.3.0 changes nothing you can see in the Windows program: the screens, buttons, Setup options and results are the same as in 0.2.4. The changes are inside, so that Convertr_svc (the Linux web edition) can share the same engine. The GPU mode here still uses NVIDIA (NVENC).

Convertr reads your Media folder, works out which files should be converted so that they play everywhere, and converts them safely: every file is written to a temporary file, checked, and only then swapped in. The original is moved to a holding folder, not deleted, and **Undo** puts it back.

## Quick start

1. **Setup**: make sure the ffmpeg line says "FFmpeg found". If it says "FFmpeg not found", press **Download FFmpeg** (see below).
2. Pick the **Media** folder and press **Scan**.
3. Narrow the table with the filters (for example *Re-encode video*, 720p) so that you try a few files first.
4. Tick **Dry run** if you only want to see the exact ffmpeg commands (they are written to the log and shown in the Details dialog; nothing is changed).
5. Press **Convert**. The first time, a confirmation dialog shows how many files, the projected saving (an estimate: a range with a most likely value, and how many files will probably be refused by the keep-if-smaller check), and where the originals go. Press **Start**.

## Main window

1. **Media folder.** One box with **Browse...**, remembered between runs, scanned recursively. The cache keeps the probe results, so a rescan takes seconds.
2. **Mode.** GPU (NVENC, fast) or CPU (x265, smaller files, slower). Locked while a run is active.
3. **Scan.** Probes every video file (3 at a time) and can be cancelled.
4. **Grid.** File, Container, Video codec, Resolution, Video kbps (`*` = estimated), Audio, Metadata, Action, **Status**, Reason, Est. savings. Double-click a row for the Details dialog (the reason for the decision and the exact ffmpeg command). **Convert works on the rows that pass the filters**; select rows first to convert only those.
5. **Convert / Pause / Stop / Stop after this file.**
   - *Pause* suspends ffmpeg and *Resume* continues.
   - *Stop* ends ffmpeg at once, deletes the partial file and leaves the original alone; the rest of the queue is kept.
   - *Stop after this file* lets the current file finish.
6. **Progress line.** Current file, percent, speed (for example 1.9x), file ETA, overall percent, GB done and overall ETA, and the saving so far.
7. **Undo...** lists the files converted in earlier runs. Undo moves the converted file to `_Convertr_Undone` (it is **never deleted**), puts the original back with its old dates, and says so when an original is no longer there.
8. **Purge Originals...** is milestone 3 and is still disabled.
9. **Export plan CSV.** Refuses to write inside the Media folder.

If Convertr was closed during a run, it offers to resume the saved queue the next time it starts.

## What happens to each file

1. Free space is checked.
2. ffmpeg writes `Name.mp4.convertr-part` next to the original.
3. The output is verified: ffprobe duration within max(1 s, 0.5%), the expected streams and codecs, language tags, no stray attachment/data streams, sensible size, and a test decode of the start, middle and end. For size re-encodes the output is kept only if it is at least 15% **and** 100 MB smaller (adjustable in Setup); otherwise the temporary file is deleted, the original stays and the row says *kept original*.
4. The original is moved to `_Convertr_Originals` (next to the Media folder, same structure), the new file takes its name (with `.mp4` if the container changed) and its dates (modified and created).
5. A line goes to the log (`Logs\Convertr_<date>_<time>.log`) and to the run's undo CSV (`Logs\undo_<date>_<time>.csv`).

If anything fails, the original is untouched, no partial file is left, and the row shows why. Convertr refuses to overwrite an existing output file.

## Actions

| Action | Meaning |
|---|---|
| Skip | Nothing to do (already HEVC, or H.264 below the bitrate threshold). |
| Remux | Container change only (video and audio copied). |
| Re-encode video | H.264 above the threshold to HEVC (size), or a legacy codec to H.264 (compat). |
| Fix audio only | Video copied, audio converted to AC3/AAC. |
| Review | HDR / Dolby Vision / 4K, unknown codec, unknown bitrate or ambiguous channel layout. Needs a human; never converted automatically. |
| Error | ffprobe could not read the file. |

## Setup window

Opens centred over the main window and always stays on the screen (it scrolls when the screen is small). Saved to `settings.json` next to the exe.

- **Encoder:** mode, x265 preset, CPU CRF, GPU CQ, legacy CRF, 8/10-bit, parallel jobs, process priority.
- **Rules:** bitrate thresholds, keep-if-smaller, output container (MP4 with faststart, or MKV), HDR/4K, interlaced and Plex Versions options.
- **Output and originals:** timestamps, holding folder (default `_Convertr_Originals`), rename in place or delete after verification (asks every run), batch size limit (0 = none), test-decode option.
- **Hardware (detected defaults):** shows your CPU, graphics card, driver, ffmpeg version and whether the GPU encoder (NVENC) really works, then the defaults Convertr chose and why. It runs by itself the first time Convertr starts and whenever the graphics card, driver or ffmpeg changes; press **Re-detect hardware** to run it again (about 5 seconds, in the background).
  - **Defaults policy:** *Auto (recommended)* uses the CPU (x265, files about 2 times smaller than the GPU at the same quality) unless the CPU is weak; *Prefer CPU quality* always uses the CPU; *Prefer GPU speed* uses the GPU whenever it works (much faster, files stay larger).
  - Settings you changed yourself (mode, preset, GPU quality, parallel jobs) are never overwritten by detection. **Reset to recommended** forgets your choices and applies the defaults.
  - **Run 20-second benchmark:** needs a scan first. It encodes 20 seconds of one of your files with the chosen mode into your temp folder (deleted afterwards), shows the speed as times real time and the estimated time for everything that would be converted now. The confirmation dialog before a run shows the same estimate.
- **FFmpeg:** the folder that holds `ffmpeg.exe` and `ffprobe.exe` (blank = auto: next to Convertr.exe, `ffmpeg` folder next to it, `%LOCALAPPDATA%\Convertr\ffmpeg`, PATH; there is no built-in folder). A status line says "FFmpeg found: version X at path" or "FFmpeg not found. Press Download FFmpeg...".
  - **Download FFmpeg:** downloads the official free Windows build (gyan.dev essentials, with fallbacks), shows progress, extracts `ffmpeg.exe` and `ffprobe.exe` into an `ffmpeg` folder next to Convertr.exe (or `%LOCALAPPDATA%\Convertr\ffmpeg` if that folder cannot be written), sets the path and shows the version. If you are offline you get a clear message and nothing changes.
  - **Download older:** opens a list of older versions; use it if the GPU test says your NVIDIA driver is too old for the newest ffmpeg builds. ffmpeg 8.1 and later need NVENC API 13.1 (a recent NVIDIA driver); 8.0.x and older work with older drivers. Or use CPU mode.
- **Theme:** System, Light or Dark.

## Where the files are

- Next to Convertr.exe (or in `%LOCALAPPDATA%\Convertr` if that folder is read-only): `settings.json`, `probe_cache.json`, `queue.json` (only while a run is unfinished) and `Logs\`.
- Next to your Media folder: `_Convertr_Originals\<Media folder name>\...` (originals) and `_Convertr_Undone\...` (files removed by Undo).
- Nothing else is written into the Media folder except the converted files themselves (and their temporary `.convertr-part` files while they are being written).

## How the projected saving is calculated
Each file gets its own estimate from its video bitrate, resolution and the chosen encoder and quality (CRF or CQ). Lean files (for example 1080p at 4-5 Mbps) save little with the GPU and may be refused by the keep-if-smaller check, so they are counted as 0 and marked "likely refused". The model is fitted on measured clips (`docs/RESULTS_SAVINGS.md`); real results vary with the content (grain and fast motion encode larger), which is why a range is shown.

## Which ffmpeg for NVENC (GeForce 10 series)
ffmpeg 8.1 and newer need NVIDIA driver 610 or newer for NVENC, which GeForce 10-series cards (GTX 1080 Ti and the like) will never get. On such a card, press **Download older** in Setup > FFmpeg and pick 8.0.1 (the list marks which versions work with your NVIDIA driver), or point Setup > FFmpeg folder at an ffmpeg 8.0.1 (or older) `bin` folder, for example `C:\Tools\ffmpeg-8.0.1\bin`. Convertr uses one ffmpeg for both modes; x265 works the same in 8.0.1. The Setup folder is checked first, so it wins over a newer ffmpeg next to the exe or on PATH. After changing it, press Re-detect hardware (or restart Convertr); Auto still recommends CPU for quality unless you choose "Prefer GPU speed".

## Setup window size
The Setup window can be resized (drag an edge or corner) or maximized. Boxes for folders stretch with the width; when the window is small the content scrolls. Save and Cancel always stay at the bottom right. The size is remembered when you press Save and is limited to the screen the next time.

## Moving Convertr to another computer
Copy the exe (settings.json, probe_cache.json and the logs are only needed if you want the same settings and history). A fresh Convertr has no built-in paths: the Media field is empty and you choose the folder with Browse. If you also copy settings.json, Convertr checks it at start-up: a Media folder that does not exist on this computer is cleared (when the file came from another computer), an FFmpeg folder that is not valid here is cleared, and the hardware is detected again. Nothing is deleted. A notice at the top of the window says what was cleared.

## Download older (FFmpeg)
Setup > FFmpeg > **Download older** opens a list of older ffmpeg versions (the official gyan.dev essentials builds, read from GitHub; a built-in list of 8.0.1, 8.0, 7.1.1, 7.0.2 and 6.1.1 is used when you are offline). Pick a row (nothing is selected at first) and press Download. Each row says which NVIDIA driver NVENC needs with that version and, when your hardware is known, whether it works with your driver. The version is saved in its own folder `ffmpeg\<version>` next to Convertr.exe (or under `%LOCALAPPDATA%\Convertr\ffmpeg` if that folder cannot be written), so versions do not overwrite each other, and Setup is pointed at it. Choosing a version you already downloaded just selects it. **Download FFmpeg** still gets the latest release. Downloads are checked by size and by SHA-256 when one is published.
