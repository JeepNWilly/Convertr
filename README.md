# Convertr

Convertr is a standalone Windows program (C#, .NET, WinForms) that scans a Plex library and works out which files should be converted so that they direct-play on a Smart TV, a Fire Stick, Plex Web and the Plex mobile app.

**Status: version 0.3.0, build 12 (scan, preview, convert, automatic hardware defaults).** Convertr reads your library, shows what it would do, and converts the files you choose: each one is written to a temporary file, verified with ffprobe and a test decode, and only then swapped in. The original is moved to a holding folder (never deleted) and **Undo** restores it. See [docs/DESIGN.md](docs/DESIGN.md) for the full plan; Purge Originals and the remaining polish are milestone 3.

## What it does

- **Scan** reads every video in the **Media** folder (one box with a Browse button, remembered, scanned recursively) with `ffprobe` (3 at a time, read-only). The results are cached in `probe_cache.json` next to the exe, so a rescan only probes new or changed files and an interrupted scan resumes where it stopped.
- **Planner** decides, for each file, one of: **Skip**, **Remux**, **Re-encode video**, **Fix audio only**, **Review** (needs a human decision) or **Error** (ffprobe could not read it). The rules are in DESIGN.md section 3.2 and the thresholds in Setup.
- **Preview table** with filters, sorting, a summary bar and a projected saving that is clearly marked as an estimate. Double-click a file for its full details and the reason for the decision.
- **Theme:** Setup has Theme: System (default), Light or Dark, applied to every window including the title bar.
- **Convert** (GPU with NVENC, or CPU with x265): queue with progress, speed and ETA, Pause/Resume, Stop, resumable after a crash, free-space check, dry run that shows the exact ffmpeg commands, a first-run confirmation, a plain-text log and an undo CSV per run.
- **Verify and swap:** duration, streams, size and test-decode checks; keep-only-if-smaller (15% and 100 MB); original moved to `_Convertr_Originals`; dates preserved; **Undo**.
- **Download FFmpeg** button in Setup (official free Windows build, with progress) and a status line that says whether FFmpeg was found.
- **Export plan CSV** writes the table to a file outside the library.

## The rules in short

| # | Condition | Action |
|---|---|---|
| 1 | ffprobe failed, no video stream, or no duration | Error |
| 2 | HDR, Dolby Vision or 4K (unless allowed in Setup); interlaced only if "Process interlaced files" is off | Review |
| 3 | Legacy video (xvid/divx, WMV/VC-1, MPEG-1/2...) | Re-encode to H.264, for compatibility |
| 4 | HEVC | Skip (or Fix audio only) |
| 5 | H.264 at or above the bitrate threshold for its resolution | Re-encode to HEVC, for size |
| 6 | Video is fine but no AAC/AC3/EAC3/MP3 audio track | Fix audio only |
| 7 | Container is not MP4/MKV, or optional cleanup of skipped files | Remux |
| 8 | Anything else | Skip |

Default thresholds: 1080p 4,000 kbps, 720p 2,500 kbps, 576p/480p 1,500 kbps, below 480p 1,200 kbps. Where the file has no per-stream bitrate, the video bitrate is estimated from the overall bitrate (shown with `*`).

## Requirements

- Windows 10 or 11, 64-bit.
- FFmpeg with `ffmpeg.exe` and `ffprobe.exe`. Setup finds it next to Convertr, in an `ffmpeg` folder next to it, or on `PATH`, or you can press **Download FFmpeg**.
- For GPU mode: an NVIDIA card with NVENC and a driver that matches the ffmpeg build (see the user guide). CPU mode needs nothing special.
- To build: the .NET 10 SDK or newer (`build.ps1` finds it on PATH or in `%LOCALAPPDATA%\Microsoft\dotnet`).

## Build and run

```
cd source
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This runs the tests, then publishes a single-file, self-contained `Convertr.exe` one level above `source`. It is about 60-70 MB, runs on any 64-bit Windows with nothing installed, and keeps its files next to itself. `build.ps1 -FrameworkDependent` makes a small exe that needs the .NET 10 Desktop Runtime instead.

Files Convertr writes (next to the exe): `settings.json`, `probe_cache.json`, `queue.json` (while a run is unfinished), `Logs\`, and a plan CSV only when you export one. In the library it only writes the converted files (and their temporary `.convertr-part` files) and the `_Convertr_Originals` / `_Convertr_Undone` folders next to the Media folder.

## Tests

```
cd source
dotnet test tests\Convertr.Tests\Convertr.Tests.csproj
```

The tests use mocked ffprobe JSON for every rule, golden ffmpeg command lines, a fake ffmpeg for the engine (verify, swap, rollback, undo, queue persistence, timestamps) and, when the earlier scan CSV is available, replay the real library through the planner. Real encodes are tested on scratch copies with the Harness (`tools/Harness`, `convert`, `quality`, `undo`), never on the library.

## More

- [docs/USER_GUIDE.md](docs/USER_GUIDE.md): using the window and Setup.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): layout, design notes, deviations from the design document.
- [docs/DESIGN.md](docs/DESIGN.md): the full design (all milestones).
- [CHANGELOG.md](CHANGELOG.md).

## License

Copyright (C) 2026 JeepNWilly. Convertr is free software under the GNU General Public License, version 3 or (at your option) any later version. It comes with no warranty. See [LICENSE](LICENSE).
