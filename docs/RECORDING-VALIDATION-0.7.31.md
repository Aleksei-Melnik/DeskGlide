# Recording validation — DeskGlide 0.7.31

Measured on 2026-10-20, Windows 11, RTX 5070, native 2560 × 1440 HDR display.
The installed recorder was closed. Test artifacts remain in private AppData or
ignored working folders; recordings, audio references, profiles and signing
keys are excluded from Git and release packages.

## Corrections

The old narrow deadline window could reject an available image, skip forward
and then repeat its successor. A real trace reproduces the defect. Matching
now conserves distinct chronological images through a bounded 20 ms timing
allowance, covered by the existing 100 ms recording playout. Sustained
oversampling retains nearest-image pacing only when the already-buffered
continuation supplies the following deadlines (up to eight). The continuation
uses fully buffered deadlines: requiring a fixed future horizon caused motion
phase changes as GPU readback age varied, and that candidate was rejected.

Windows can publish identical pixels with different presentation timestamps.
Exact NV12 comparison removes these consecutive duplicates before selection,
retaining the image's first timestamp and preserving format changes. In the
animated game lobby, the preceding candidate repeated 324 of 3,600 decoded
adjacent images. Filtering plus chronological/continuation selection removes
the measured repeats below. Counters alone did not establish this result:
the encoded image content was independently decoded and compared.

Acquisition now uses 120/240 Hz for 60/120 FPS, replacing the 72/144 Hz beat
that created alternating motion steps. Brief 8/4 ms desktop-presentation waits
avoid missing updates just after zero-timeout polls. GPU readback stays
asynchronous. Rational pacing deadlines retain fractional QPC ticks; tests
cover exact one-hour endpoints at 60/120/240 Hz.

NV12 packing uses a separate worker with immutable, timestamped/generation-tagged
references. Its queue is limited to eight images and 64 MiB (or one larger
image); overload drops the oldest pending reference with accounting. Clear,
shutdown, blocked conversion and obsolete generations have regression checks.
AVX2 packs 16 pixels at a time; pixel tests cover vector boundaries and tails
and separate SSSE3/scalar fallback runs. The NDI HDR-to-SDR transform and
limited-range BT.709 pixel codes are retained.

## Final desktop motion and simultaneous NDI

A fullscreen D3D11 source presents a moving pattern at 240 Hz. A source-image
ID and complement at both top and bottom must agree throughout the export.
Each run captures for 60 seconds after warm-up and exports 55 seconds, with an
actual local NDI receiver. Present calls/DXGI statistics are diagnostics rather
than independent proof of physical scanout. The table describes decoded motion.

| Export + NDI | Frames | Repeated / invalid IDs | Ideal motion steps | Step standard deviation |
| --- | ---: | ---: | ---: | ---: |
| AV1-phase2-2560-120 | 6600 | 0 / 0 | 99.864% | 0.05505 |
| AV1-phase3-2560-120 | 6600 | 0 / 0 | 99.879% | 0.04924 |
| HEVC-3840-120 | 6600 | 0 / 0 | 99.864% | 0.06031 |
| H264-1920-120 | 6600 | 0 / 0 | 99.909% | 0.04606 |
| H264-1920-60 | 3300 | 0 / 0 | 100.000% | 0.00000 |

An ideal step advances two source IDs at 120 FPS or four at 60 FPS. Standard
deviation is measured in source-image intervals (1/240 second). All 29700
decoded frames have zero readback/conversion queue drops, exhausted histories
or recording errors. NDI received and matched source IDs without receive
errors. Two separate AV1 starts exercise different capture/output phases.
Motion statistics were also recalculated from the retained decoded top/bottom
marker bytes; each runtime gate was checked before advancing to the next case.

## Final codecs, SDR, audio and saves

All 24 combinations of AV1/HEVC/H.264, 720p/1080p/1440p/4K and 60/120 FPS
passed on the final selection path. Each uses an FP16 HDR GPU scene and three
consecutive saves. All 35969 decoded frames have zero repeated IDs and zero
lost available source images. Save boundaries are contiguous, without overlap;
every case has one encoder session and zero history/conversion queue drops.
Mixed/separate AAC, dimensions, container tags and elementary-stream SDR
limited-range BT.709 headers pass. Twelve native/scaled/letterbox hardware
color cases match the NDI-derived flat SDR patches exactly. This does not claim
lossless equality of compressed textures.

Video/audio share an epoch anchored to an actual source image after the initial
timestamped local audio packet. This fixes a measured 5 ms startup silence;
unavailable/silent sources and static desktops retain bounded startup waits.
The final 120-second AV1/native1440p120 actual-WASAPI run and two continuing
saves validate 16853 decoded images, frame conservation, contiguous exports
and no missing game-audio samples. Independent timestamped WASAPI/coded-tone
comparison gives a maximum A/V offset of 2 ms, spread 2 ms.

The preceding epoch/audio-ready revision also completed a 600-second recording
and two continuing saves: 80,000 decoded frames, zero repeated/lost available
images, one encoder session, zero history/queue drops, and zero missing samples
across 32,417,600 game-audio sample positions. Maximum independently measured
A/V offset and spread were 2 ms. The shorter final run rechecks the later image
filter/selection changes; the earlier ten-minute measurement is not relabeled
as a run of those later changes.

## Final game measurement and limits

Two fresh minutes of HEVC/native1440p120 capture the animated Phasmophobia lobby.
There are zero selected-image repeats, exhausted histories or missing audio
samples. The last 30 seconds decode to 3,600 frames with zero near-identical
adjacent images at both 0.05 and 0.10 mean luma-change thresholds (320 × 180
area reduction). These approximate content checks complement the known-ID
fixture; they cannot prove every game/rendering condition.

These results do not establish universal NVIDIA App equivalence or a matched
120-FPS game-impact benchmark. Real rendering/source stalls can still require
CFR output to hold its last image; interpolation does not conceal absent
images. Other GPU generations and longer sessions remain outside this run.
No Windows security, display, HDR or game settings were changed. Test windows
and the game launched for measurement were closed afterward. Existing signing,
settings/pairing, app-folder attribution and protected-folder updater behavior
are retained and covered by the release regression gate.
