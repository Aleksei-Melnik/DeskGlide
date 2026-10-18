# Recording validation — DeskGlide 0.7.28

Hardware: Windows 11, RTX 5070 (driver 617.42), native 2560 × 1440 at
approximately 500 Hz, HDR enabled, Windows SDR white 240 nits. Tests used
isolated replay directories. The installed recorder/NDI sender were saved and
paused to avoid a second active encoder. Private recordings, audio references,
profiles and credentials are excluded from source/release artifacts.

## Defects and corrections

- A preceding-only source selector repeated a frame and discarded the following
  available frame when independent capture/record clocks straddled small timing
  variations. Chronological selection now tolerates 0.75 output intervals while
  preserving real source gaps. Pacing retains its phase after missed slots.
- Immediate staging-buffer mapping stalled CPU capture behind submitted GPU
  work. Three bounded, nonblocking readback buffers preserve presentation
  timestamps. Replay acquisition has 20% headroom (72/144 Hz for 60/120 output)
  and drains completed copies each poll. NDI remains 60 Hz. This adds bounded
  delivery latency and is not a zero-copy game-to-NVENC implementation.
- Per-pixel managed resizing on the recording deadline made native 1440p to
  4K/120 fall seconds behind. Native NV12 now reaches FFmpeg; NVIDIA uses
  preflighted CUDA scaling directly into NVENC, with optimized software scaling
  as fallback. Aspect ratio and limited-range black letterboxing are preserved.
  The overload watchdog uses elapsed time instead of written-frame count.
- The supplied AV1 files had BT.709 MP4 tags but unspecified elementary-stream
  primaries/transfer. Raw input frames and encoded output now both carry
  limited-range BT.709. Pixel conversion remains the existing NDI HDR-to-SDR
  transform, including SDR-white compensation; recordings contain SDR video.
- Saving used foreground context at Save time and blacklisted ordinary apps
  as Desktop. A bounded activity timeline now chooses the dominant app in the
  actual exported interval. Save controls are transparent, File Explorer is
  distinct from Desktop, packaged app hosts are resolved, and disabling app
  grouping saves directly into the chosen directory.

## Encoder/scaling matrix

All **24** combinations passed: AV1, HEVC and H.264; 1280 × 720,
1920 × 1080, 2560 × 1440 and 3840 × 2160; 60 and 120 FPS. Every case used a
changing native 1440p FP16 HDR GPU scene, production recorder and three
consecutive saves. The 60-FPS cases used mixed synthetic audio; 120-FPS cases
used three separate tracks. Each ran 15 seconds before the first export.

Decoded frame IDs verified chronology, conservation of available source images,
and save boundaries without overlap/gaps. AAC continuity, output dimensions,
container tags and elementary-stream BT.709 limited-range headers passed.
Every case used one encoder session and zero exhausted frame histories.
The source generator itself skipped one frame in AV1/native120 and two in
H.264/4K120; those gaps were accounted for separately from recorder losses.

Twelve additional hardware SDR tests covered AV1/HEVC120 and H.26460 in mixed
and silent modes, both native 640 × 360 and CUDA-scaled/letterboxed 1280 × 960.
All had zero repeated IDs/source skips, correct elementary headers and black
bars. Measured flat-patch Y/U/V code difference from NDI-derived input was zero.
This is a flat-patch assertion, not lossless equivalence of compressed textures.
The hosted software regression also exercises scaled/letterboxed libx264.

## Desktop acquisition and game comparison

The visible D3D11 240-Hz fixture carries complementary frame markers at the top
and bottom. Both must agree throughout the export; partially covered tests and
older commands that accidentally used CLI defaults are excluded.

| Valid export | Frames | Repeated pixel IDs | Invalid markers | Capture rate |
| --- | ---: | ---: | ---: | ---: |
| H.264 1080p60 | 1,500 | 0 | 0 | 72 Hz |
| HEVC native 1440p120 | 3,000 | 13 (0.43%) | 0 | 144 Hz |

The 120-FPS run repeated an input timestamp only once. Pixel repetitions can
also come from the fixture's presentation/DWM scheduling; configured output
FPS does not establish the same number of distinct source images. Both runs
had no exhausted frame history. This is a measured remaining limitation.

NVIDIA App and DeskGlide recorded the same static-camera animated game lobby
sequentially, both H.264 1440p60. Neither final 30-second sample contained
near-identical adjacent decoded frames at mean-luma thresholds of 0.05/0.10
on a 320 × 180 reduction. DeskGlide supplied approximately 71.2 captured
images/s and repeated one input timestamp across 3,833 encoded frames in the
full diagnostic. Quality/bitrate were not identical. This establishes a limited
60-FPS comparison, not subjective equivalence, game-FPS impact or 120-FPS
NVIDIA parity. PresentMon could not collect ETW metrics with the current user
permissions; no privilege/group/security settings were changed to obtain them.
NVIDIA preferences were restored and the test game was closed.

## Long recording, audio and NDI follow-up

The AV1 native 1440p120 run exported a full 600-second replay followed by
61.9- and 8.258-second consecutive saves while recording continued. All 80,419
exported frames were decoded: zero available source images lost, no save gap
or overlap, one encoder session and zero history misses. The first clip had
10 repeated IDs against 11 unavailable source IDs; the producer's source gaps
were distinguished from recorder losses. Elementary SDR headers and three AAC
tracks passed. Active game audio had no low-energy holes; the inactive mic and
extra tracks correctly contained silence.

Independent WASAPI reference capture checked the coded-amplitude tone against
source/QPC frame IDs. There were zero missing local sample positions out of
32,516,000 read. Continuous decoding measured +4 ms at both the beginning and
end of the 600-second clip, -6 ms at both measured positions in the next save,
and -4 ms in the third. Maximum absolute offset was 6 ms; offsets differed by
10 ms across save boundaries, without within-clip accumulation. Fast input
seeking was excluded from this measurement: raw PCM has no timestamp to retain
any late seek start. All correlation windows came from continuous decoded audio.

Production raw history plus its reuse cache sampled 63.3–89.6 MiB, ending near
68.6 MiB. The diagnostic intentionally retained an independent reference PCM
copy, which grows with test duration; that memory is not a production replay
buffer or a leak. Full-app/driver/NDI memory is outside this raw-history metric.

The HEVC CUDA-upscaled 4K120 run exported 300 seconds followed by 10.8 and
2.442 seconds. All 37,589 exported frames were decoded with zero repeated IDs,
zero available images lost and zero producer skips. Save boundaries, mixed
synthetic AAC continuity and SDR elementary headers passed; one encoder session,
zero history misses. Raw history/cache sampled approximately 79.1–84.4 MiB.

The H.264 CUDA-downscaled 1080p120 silent run exported 300 seconds followed by
18.358 and 3.292 seconds. All 38,598 frames were decoded with zero repeated IDs,
zero producer skips, zero available images lost and continuous save boundaries.
The exports contained no audio track, retained SDR elementary headers and used
one encoder session without history misses. Raw history/cache was 73.8–84.4 MiB.

Three simultaneous desktop-capture/recording/local-NDI-receive runs passed.
The test scene was explicitly activated before the measured export; an earlier
run obscured by the live KVM window was rejected and excluded. Every exported
top/bottom complementary marker decoded correctly, with no recorder error or
history exhaustion. NDI stayed at 60 FPS and replay acquisition at 72/144 Hz.

| Recording | Recorded frames / repeated IDs | Matched NDI frames / repeated IDs | NDI age median / p95 |
| --- | ---: | ---: | ---: |
| H.264 1080p60 | 1,500 / 1 | 1,653 / 12 | 46.2 / 56.6 ms |
| AV1 1440p120 | 3,000 / 10 | 1,699 / 1 | 35.9 / 44.3 ms |
| HEVC 4K120 | 3,000 / 6 | 1,634 / 1 | 36.2 / 44.8 ms |

The receiver matches decoded marker IDs to original captured desktop timestamps.
These are local receive ages, not physical display-to-display or LAN/OBS latency.
Receiver startup/discovery reduces counted frames; repetitions include source
presentation/DWM timing. The remaining 120-FPS pixel repeats are disclosed,
not hidden by constant-rate packet timestamps. Test senders stop before the
scene closes, and all clips remain in ignored local investigation directories.

## Regression and coverage limits

Local release builds have zero compiler warnings/errors. Deterministic cadence,
folder attribution, GPU ordering/pixel equivalence/resize reset, signed update,
audio clock and save/delivery/reconfiguration checks passed. Hosted checks run
against folder and standalone packages. The complete suite passed on
`3cf5d27` ([run](https://github.com/Aleksei-Melnik/DeskGlide/actions/runs/39097625557));
the signed-release workflow repeats it on the final release commit. A hosted
SDR fixture initially exported only 103 frames after cold startup
consumed a fixed sleep. It now waits for four seconds of actual closed media
with a bounded startup deadline. Actual offered-image conservation and source
gaps are checked separately from frame order, repetition, color and audio.

The 0.7.27 narrow elevated-input helper and existing update signing key are
retained. These recording tests do not replace physical two-PC input testing.
Other GPU generations, physical Intel hardware, resolutions above the UI's 4K
option, two-hour soaks and all games are outside this verification. Do not
claim universal NVIDIA App parity from packet timestamps or encode throughput.
