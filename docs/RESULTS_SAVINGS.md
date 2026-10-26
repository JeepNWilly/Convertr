# Measured results behind the projected savings (build 7, 17 Oct 2026)

All encodes were made on scratch copies or on clips cut (stream copy, read-only) from library files into `%USERPROFILE%\ConvertrScratch`; nothing was ever written to the library.
Encoders: x265 `-preset slow -crf 21` (ffmpeg 9.0.2), NVENC `hevc_nvenc -preset p7 -tune hq -rc vbr -cq N -b:v 0 -spatial-aq 1 -rc-lookahead 32` (GTX 1080 Ti, ffmpeg 8.0.1). Raw data: `docs/data/*.csv`, fit script `docs/data/fit.py`.

## 1. GPU driver update and NVENC
Driver 581.80 to **582.78**. `hevc_nvenc` still fails with ffmpeg 9.0.2 and 8.1.2 ("Required: 13.1 Found: 13.0", "the minimum required Nvidia driver for nvenc is 610.00 or newer"). The GTX 10 series is on the last (580) driver branch, so **no driver update can make ffmpeg 8.1 and newer use NVENC on this card**. ffmpeg 8.0.1, 8.0 and 7.1.1 work (HEVC and H.264). Auto therefore still chooses CPU x265.

## 2. NVENC options (Pascal, ffmpeg 8.0.1, 90 s clips, CQ 27)
| Variant | Series_J size / VMAF | Series_C S10E16 size / VMAF | Note |
|---|---|---|---|
| base (p7, tune hq, spatial-aq, lookahead 32) | 51.3% / 96.76 | 58.1% / 95.38 | current setting |
| -multipass fullres | 51.1% / 96.74 | 57.3% / 95.35 | no gain, 1.5x slower |
| -multipass qres | 51.2% / 96.73 | 57.3% / 95.34 | no gain |
| -temporal-aq 1 | fails | fails | not supported by Pascal HEVC ("No capable devices found") |
| -aq-strength 12 | 60.3% / 96.39 | 65.8% / 95.02 | larger and worse |
| base CQ 28 | 43.2% / 96.49 | 49.6% / 94.98 | |
| base CQ 29 | 37.0% / 96.22 | 42.7% / 94.56 | |
x265 CRF 24 gives 18% / VMAF 96.27 (Series_J) and 22% / 94.72 (Series_C): at equal VMAF x265 is about **2.0 times smaller** than NVENC (earlier "2.5x" compared different VMAF values). No NVENC option gives a better trade-off than the current ones; **keep GPU CQ 27** (CQ 28 is the "smaller" option at about 0.3 VMAF less).

## 3. Size model (what the estimates are based on)
Clips (30 s from the middle of real library files, plus the 90 s clips of the first sweep), video stream only, output video bitrate:

| Clip | Res | Source kbps | x265 CRF21 kbps (% of source) | NVENC CQ27 kbps (%) | NVENC CQ24 kbps (%) |
|---|---|---|---|---|---|
| series_a.s05e09.1080p.mkv | 1080p | 4963 | 2787 (56%) | 3647 (73%) | 6271 (126%) |
| Movie_A 2016 1080p.mkv | 1080p | 5377 | 5846 (109%) | 7358 (137%) | 10682 (199%) |
| Series_C S09E12 - Episode Title | 1080p | 5608 | 3523 (63%) | 5202 (93%) | 8172 (146%) |
| Series_B.S06E09.1080p.mkv | 1080p | 6000 | 1468 (24%) | 3729 (62%) | 5819 (97%) |
| Series_C S10E16.mp4 | 1080p | 6597 | 2501 (38%) | 3908 (59%) | 6063 (92%) |
| Series_D S01E02 Episode Title 1080p | 1080p | 7010 | 2580 (37%) | 5468 (78%) | 8810 (126%) |
| Series_E 2026 - S01E05 - Episode Title | 1080p | 10418 | 5422 (52%) | 5373 (52%) | 8656 (83%) |
| Series_F.S01E05.720p.mkv | 720p | 1676 | 1712 (102%) | 2340 (140%) | 3549 (212%) |
| Series_G.S03E05.720p.mkv | 720p | 1816 | 880 (48%) | 1387 (76%) | 2283 (126%) |
| Series_H S02E12 Episode Title.mkv | 720p | 1954 | 1323 (68%) | 1672 (86%) | 2652 (136%) |
| Series_I S01E07 Episode Title 720p | 720p | 3047 | 1244 (41%) | 2174 (71%) | 3346 (110%) |
| Series_C S01E14 - Episode Title | 720p | 3275 | 1728 (53%) | 2143 (65%) | 3276 (100%) |
| series_b.s03e19.720p.mkv | 720p | 3657 | 1177 (32%) | 2425 (66%) | 3708 (101%) |
| Series_C S10E13.mp4 | 720p | 3761 | 2233 (59%) | 2500 (66%) | 3799 (101%) |
| Movie_B.2014.mkv | 720p | 3796 | 2337 (62%) | 2597 (68%) | 3930 (104%) |
| Series_J 2023 - S03E03 - Episode Title W | 720p | 4246 | 1450 (34%) | 2156 (51%) | 3400 (80%) |
| series_k.s04e13.internal.720p.mkv | 720p | 4888 | 2307 (47%) | 2597 (53%) | 3930 (80%) |
| series_l.s01e07.720p.mkv | 720p | 6883 | 1873 (27%) | 2428 (35%) | 3892 (57%) |

Output bitrate depends mostly on the content, only weakly on the source bitrate. Fit (least squares on logs, 20 clips each; bitrate per 720p-sized frame, so 1080p counts as 2.25 frames):
- x265 CRF 21: output = 31.3 * source^0.482 (sd of the log 0.35: range x0.71 to x1.42)
- NVENC CQ 27: output = 97.8 * source^0.387 (sd 0.21: range x0.81 to x1.23)
- each 3 steps of CRF change the size by x0.61 (measured 0.57-0.66); each 3 steps of CQ by x0.64 (measured 0.62-0.64, 20 clips)
Lean or grainy sources can grow: the Movie_A clip (1080p, 5.4 Mbps grainy film) came out at 109% of the source with x265 CRF 21 and 137% with NVENC CQ 27. In the whole-file M2 run (CQ 24) Series_J saved only 8.5%, Series_C S10E16 6.6%, and Series_P, Series_C S10E13 and Series_Q were refused. The first-run model checks: Series_P CPU model 49% saved, measured 48.7%; Series_J GPU CQ24 model 95% of source, measured 91.5%; Series_L GPU CQ24 model 58%, measured 53.2%.
Excluded: three clips whose stream-copy cut had decode errors (Series_M S02E03, Series_C S03E14, Series_N S02E01). The first run's GPU rows for Series_E S01E05 and Series_C S01E14 were wrong and were repeated (durations checked); the repeated values are in the table.

The planner uses the model per file: saving = video bytes x (1 - output / source), shown as a range (optimistic, expected, pessimistic). A bound whose saving would fail the keep-if-smaller rule (15% and 100 MB by default) counts as **0** because the encode would be refused; a file whose expected saving is 0 is shown as "likely refused".

## 4. Projected saving for the real library (3,136 files in `D:\Media`, from the local probe cache, no access to the library; defaults: 15% / 100 MB, CRF 21, CQ 27)
670 size candidates, 1,179 GB (Movies 78 / 312 GB, Series 592 / 867 GB).

| | Movies low / likely / high | Series low / likely / high | Total low / likely / high | Likely refused |
|---|---|---|---|---|
| **CPU x265 CRF 21** | 111 / 159 / 192 GB | 262 / 416 / 521 GB | **373 / 575 / 713 GB** | 0 |
| **GPU NVENC CQ 27** | 69 / 111 / 142 GB | 117 / 244 / 354 GB | **186 / 355 / 496 GB** | 68 files (102 GB) |

(Previously the flat percentages gave about 370-500 GB for CPU and 250-430 GB for GPU from 1,238 GB of candidates; the flat 20-35% for GPU ignored that 1080p files near the threshold shrink little or grow.)
Full report (`Convertr.Harness savings`):
```
cache entries under D:\Media: 3136
Re-encode size=670  compat=249  fix audio=4  remux=0  skip=2213  review=0  error=0
size candidates: 1,179 GB in 670
projected saving Gpu Movies: candidates   78 (   312 GB)  low    69  likely   111  high   142 GB   likely refused   0 (    0 GB)
projected saving Gpu Series: candidates  592 (   867 GB)  low   117  likely   244  high   354 GB   likely refused  68 (  102 GB)
projected saving Gpu Total : candidates  670 ( 1,179 GB)  low   186  likely   355  high   496 GB   likely refused  68 (  102 GB)
   Gpu 1080p 10.5-12.0 Mbps: n=  3      32 GB  likely saving    14 GB (45%)  refused 0
   Gpu 1080p 3.0-4.5 Mbps: n= 23      45 GB  likely saving     4 GB (9%)  refused 18
   Gpu 1080p 4.5-6.0 Mbps: n= 79     164 GB  likely saving    20 GB (12%)  refused 47
   Gpu 1080p 6.0-7.5 Mbps: n= 27      81 GB  likely saving    25 GB (31%)  refused 0
   Gpu 1080p 7.5-9.0 Mbps: n= 41     134 GB  likely saving    49 GB (36%)  refused 0
   Gpu 1080p 9.0-10.5 Mbps: n= 12      52 GB  likely saving    23 GB (44%)  refused 0
   Gpu 480p  1.5-3.0 Mbps: n= 14      21 GB  likely saving     9 GB (41%)  refused 1
   Gpu 576p  1.5-3.0 Mbps: n=  2       3 GB  likely saving     1 GB (32%)  refused 0
   Gpu 720p  1.5-3.0 Mbps: n=117     142 GB  likely saving    33 GB (23%)  refused 2
   Gpu 720p  3.0-4.5 Mbps: n=312     396 GB  likely saving   132 GB (33%)  refused 0
   Gpu 720p  4.5-6.0 Mbps: n= 40     109 GB  likely saving    46 GB (42%)  refused 0
projected saving Cpu Movies: candidates   78 (   312 GB)  low   111  likely   159  high   192 GB   likely refused   0 (    0 GB)
projected saving Cpu Series: candidates  592 (   867 GB)  low   262  likely   416  high   521 GB   likely refused   0 (    0 GB)
projected saving Cpu Total : candidates  670 ( 1,179 GB)  low   373  likely   575  high   713 GB   likely refused   0 (    0 GB)
   Cpu 1080p 10.5-12.0 Mbps: n=  3      32 GB  likely saving    18 GB (57%)  refused 0
   Cpu 1080p 3.0-4.5 Mbps: n= 23      45 GB  likely saving    17 GB (38%)  refused 0
   Cpu 1080p 4.5-6.0 Mbps: n= 79     164 GB  likely saving    69 GB (42%)  refused 0
   Cpu 1080p 6.0-7.5 Mbps: n= 27      81 GB  likely saving    40 GB (50%)  refused 0
   Cpu 1080p 7.5-9.0 Mbps: n= 41     134 GB  likely saving    71 GB (53%)  refused 0
   Cpu 1080p 9.0-10.5 Mbps: n= 12      52 GB  likely saving    30 GB (58%)  refused 0
   Cpu 480p  1.5-3.0 Mbps: n= 14      21 GB  likely saving    11 GB (54%)  refused 0
   Cpu 576p  1.5-3.0 Mbps: n=  2       3 GB  likely saving     2 GB (51%)  refused 0
   Cpu 720p  1.5-3.0 Mbps: n=117     142 GB  likely saving    61 GB (43%)  refused 0
   Cpu 720p  3.0-4.5 Mbps: n=312     396 GB  likely saving   198 GB (50%)  refused 0
   Cpu 720p  4.5-6.0 Mbps: n= 40     109 GB  likely saving    58 GB (53%)  refused 0
```
