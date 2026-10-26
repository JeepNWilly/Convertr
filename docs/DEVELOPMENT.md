# Development notes

## Layout

- `src/Convertr.Core` (net10.0): models, ffprobe parser, planner (pure functions), settings, probe cache, scanner, and the engine: `FfmpegCommandBuilder` (plan item to ffmpeg arguments, pure), `ConversionEngine` (queue, worker, pause/stop, free-space check), `FfmpegRunner` (process, `-progress pipe:1`, suspend/resume, kill tree), `Verifier`, `Swapper` + `HoldingArea`, `UndoJournal` + `UndoService`, `QueueStore` (`queue.json`, atomic writes), `RunLog`, `ChapterFile`, `FfmpegDownloader`, `DialogGeometry`. No UI.
- `src/Convertr` (net10.0-windows WinForms): MainForm, SetupForm, DetailsForm, `RunDialogs` (confirmation and undo dialogs, `AppPaths`), `DialogPlacement` (centre over the owner and clamp to the working area), `Theming` (Dark/Light/System) and helpers.
- `tests/Convertr.Tests`: xunit. Mocked ffprobe JSON (`Ffp.cs`) covers every planner rule; `RealScanCsvTests` checks counts against `library_formats.csv` (skipped if the CSV is absent).
- `tools/Harness`: dev-only console tool, not shipped: `scan`, `shots`, `runshots` (screenshots of the run, confirmation, undo and Setup/download states), `convert` (real end-to-end run on a scratch COPY of a library: copies, scans, plans, converts, writes a per-file CSV; it refuses a Z: path), `quality` (ssim/psnr/libvmaf of each converted file against its original in the holding folder) and `undo`.
- `build.ps1`: runs tests, bumps BUILD_NUMBER, publishes single-file win-x64 to `..\Convertr.exe`. Default is self-contained (~50 MB, runs anywhere); `-FrameworkDependent` gives a small exe that needs the .NET 10 Desktop runtime. Also `-NoTest`, `-NoBump`. It needs the .NET 10 SDK and looks for it on PATH, then in `%LOCALAPPDATA%\Microsoft\dotnet`, `%USERPROFILE%\.dotnet` and Program Files.

## Planner rule order

1 error, 2 HDR/Dolby Vision/4K review (and interlaced review only when "Process interlaced files" is off), 3 legacy codec to H.264, 4 HEVC skip, 5 H.264 at/above per-resolution threshold to HEVC, 6 audio-only fix, 7 remux, 8 skip.

## Deviations from DESIGN.md

- Targets .NET 10 (`net10.0` / `net10.0-windows`), as in the design.
- Rules 4 and 8 fall through to Remux (7) when the container is not MP4/MKV (as selected output container applies).
- Rule 3's AVI/WMV/MPG test applies only to non-h264/hevc codecs; H.264 or HEVC in AVI becomes Remux.
- Unknown codec, unknown bitrate and ambiguous audio layouts (0/3/4/5/7 or >8 channels, only when that audio must be converted) become Review.
- Rule 6 triggers only when no track is a compatible codec.
- ffprobe warnings are flagged but do not change the action.
- `Plex Versions` folders are skipped by default (setting `IncludePlexVersions`); with it on the library is 3,140 files, off 3,137.
- Interlaced files are processed (decision 2026-10-16), not Review: re-encode actions carry the note "interlaced: deinterlace (bwdif) will be applied in conversion" and `PlanItem.Deinterlace` is true so M2 adds `bwdif`. The Setup option "Process interlaced files (deinterlace)" (`Settings.ProcessInterlaced`, default on) switches them back to Review under rule 2. The old `AllowHdr4kInterlaced` setting became `AllowHdr4k` (HDR/Dolby Vision/4K only). Interlaced files that are skipped or only remuxed are not flagged. The two Series_P S04 files are therefore size re-encodes (828 size candidates, 0 Review).
- One **Media** folder (`Settings.MediaFolder`) replaces the Movies/Series list. The scanner still takes a list of roots (tests use several); the app passes one, labelled with the folder name (that label is the `Library` column in the CSV export). Old `settings.json` files with `Libraries` migrate once to the Movies path (or the first path); `Libraries` is never written back.
- **Theme** (`Settings.Theme`: System, Light, Dark; default System). `Theming.cs` holds the two palettes, applies colours recursively (buttons, text boxes, combo boxes, numeric boxes, check/radio buttons, group boxes, labels, grids, progress bars, tool tips, menus), sets native dark scroll bars with `SetWindowTheme("DarkMode_Explorer")` and the title bar with `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`. Forms call `Theming.Register(this)` at the end of their constructor. System reads `HKCU\...\Themes\Personalize\AppsUseLightTheme` and re-applies live on `UserPreferenceChanged`. Labels use `Tag` = "muted" / "good" / "bad" for semantic colours. The "no row selected" rule is unaffected by the theme (selection is cleared after every bind in both themes).
- `settings.json` and `probe_cache.json` live next to the exe.

## Engine notes

- Job states: Pending, Encoding, Verifying, Swapping, Done, NotWorthIt, Failed, Skipped. `queue.json` is rewritten atomically after every state change; on restart unfinished Encoding/Verifying jobs go back to Pending, half-done swaps are finished or rolled back from the undo journal, and leftover temporary files are deleted (a job whose temporary file is still there at the start is skipped with a message rather than overwritten).
- Swap order: original to the holding folder, then the verified part file to the final name, then timestamps. A failed step moves the original back. The undo CSV (`Logs\undo_*.csv`, append-only, columns RunId, TimeUtc, Step, Status, OriginalPath, HoldingPath, NewPath, OrigSize, NewSize, OrigMtimeUtc, OrigCtimeUtc, MediaFolder, Mode, Note) is written before each step.
- Pause = `NtSuspendProcess` on the ffmpeg process; Stop = kill the process tree, delete the part file, leave the rest queued.
- The decode test (`ffmpeg -v error -f null`) runs on three 20 s slices (the whole file when it is under 2 minutes); the messages "non monotonically increasing dts" and the QuickTime chapter-reference note are ignored because they are harmless in real files.
- Tests for the engine use a fake runner (`FakeFfmpeg` in `ConversionTests.cs`) that writes a file and reports progress, so no real ffmpeg is needed; real encodes are done with the Harness on scratch copies.
- `ENCODER_OPTIONS_VERIFIED.txt` lists the encoder and filter options checked against ffmpeg 9.0.2 and 8.0.1.

## Deviations from DESIGN.md (milestone 2)

See DESIGN.md section 10.

## Hardware detection (0.2.1)
- `Hardware.cs`: models (`HardwareProfile`), parsers for the CIM JSON and `nvidia-smi` CSV (`HardwareParsers`, tested with sample text), `IHardwareProbe` (fake in tests), `HardwareDetector` (CPU, GPU, encoder tests through `FfmpegLocator.TestEncoderAsync`). `HardwareProfile.Signature()` (GPU names + drivers + ffmpeg version) decides about automatic re-detection.
- `HardwareDefaults.cs`: pure `Decide(profile, policy)`, `Apply` (skips names in `Settings.UserModified`), `TunedSettings.MarkUserChanges` (called by Setup on Save and by the Mode radios), `SpeedEstimator`. `Benchmark.cs`: `BenchmarkRunner` reuses `FfmpegCommandBuilder.Build` with a temp output and `-ss/-t` before the input.
- Start-up check: `MainForm.CheckHardwareAsync` (off in the harness through `MainForm.DisableHardwareAutoDetect`).
- Harness: `Convertr.Harness hardware --out DIR [--ffmpeg FOLDER] [--policy auto|cpu|gpu] [--bench FILE] [--shots true]`.
- NVIDIA driver number: the last 5 digits of the Windows version (32.0.15.8180 is 581.80). WMI AdapterRAM is 32-bit, so VRAM comes from nvidia-smi.

## Safety

The scanner only enumerates and calls ffprobe (ArgumentList, no shell quoting). It never opens files for write. Verified by hashing the full file listing before and after a real scan. The engine only writes when the user presses Convert and confirms; every ffmpeg call uses ArgumentList. End-to-end tests run on scratch copies only, and the Harness refuses a Z: path.

## Dev sync workflow

Master source is on the build box; it is mirrored to C:\Projects\Convertr\source via tar + SHA-256 manifest and verified after copy.

## Savings model (0.2.2)
`SavingsModel` (Convertr.Core) holds the calibration constants (`Cpu`, `Gpu`). To refit: run the clip sweeps (see `docs/data/*.csv`), run `python3 docs/data/fit.py`, copy the A, B, spread and step values into `SavingsModel.cs`, update the `Measured` table in `SavingsModelTests`, and regenerate the library report with `Convertr.Harness savings --cache <probe_cache.json> --root <media root> --out <dir>` (reads only the local probe cache).

## Platform seams (0.3.0)
Everything that differs between Windows and Linux sits behind `Convertr.Core.PlatformServices.Current` (`Platform.cs`, `PlatformLinux.cs`): app paths, process suspend/resume, file times, CPU/GPU detection, exe names, path comparison (`PathCompare`). Use `PathCompare.Equal/StartsWith/Comparer` for file paths and `PlatformServices.Current.FileTimes` for creation times; do not call `OperatingSystem.IsWindows()` or P/Invoke directly in Core logic. Every ffmpeg/ffprobe `ProcessStartInfo` must call `FfmpegEnvironment.Apply(psi)`. The swap goes through `IJobStorage` (`InPlaceStorage` = the original `Swapper` behaviour).

Refactor safety net: `GoldenPlanTests` replays the real library scan (`library_formats.csv`) through the planner, savings model and command builder. Run it before and after a Core change with `CONVERTR_GOLDEN_OUT=before.txt` / `after.txt` and compare the files (they must be identical unless the change is meant to alter plans). The reference hash of the 0.3.0 dump is in PROGRESS notes, not in the repository.
