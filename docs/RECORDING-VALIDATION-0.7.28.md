# Recording changes awaiting gameplay validation

Status: candidate 0.7.28, not published. The user finished playing and explicitly
made this PC available for comprehensive testing. They also requested shutdown
after fixes, validation and publication finish. The previous testing pause is
revoked. Do not claim NVIDIA App parity without the measured comparison.

## Findings and implemented fixes

- The supplied AV1 clips are 2560 x 1440 at 60 FPS. Their packet timestamps
  advance regularly; that does not establish distinct captured images. One
  clip includes video-player controls inside the recording, so a recording of
  playback cannot be treated as a controlled game-capture benchmark.
- MP4 metadata says BT.709, but AV1 sequence headers report unspecified
  primaries/transfer. The same FFmpeg/NVENC fixture reproduces that discrepancy.
  Setting color tags on raw input frames, before `-i`, fixes the sequence
  headers. The established NDI HDR-to-SDR pixel transform is retained.
- Strict preceding-frame selection can create alternating repeats and drops
  when capture timestamps jitter around independently phased recording slots.
  The new bounded selection preserves chronological samples across that jitter.
- Immediate staging-buffer Map serializes CPU capture behind GPU work. A
  bounded, nonblocking readback queue removes that immediate wait. It adds
  delivery latency; this is not a zero-copy NVENC implementation.
- Save-folder selection previously used the foreground app at Save time and
  classified multiple ordinary apps as Desktop. A bounded activity timeline
  now selects the dominant app in the actual exported interval. Grouping-off
  behavior, File Explorer and older packaged-app frame hosts are addressed.
- Scaling to a different recording resolution used a per-pixel C# resize on
  the writer deadline. A 1440p-source to 4K/120 test fell seconds behind. Native
  frames now reach FFmpeg unchanged; NVENC uses preflighted CUDA scaling, with
  an optimized software fallback. An isolated 4K/120 HDR-source HEVC run then
  passed decoded source-frame conservation, three save boundaries, SDR headers
  and synthetic mixed audio with zero source skips/history misses/restarts.

## Completed checks

- Release build: no warnings/errors. Deterministic 60/120 FPS jitter selection,
  folder attribution and audio-timeline tests passed locally.
- Real D3D11 SDR/HDR readback fixtures matched synchronous NDI pixels, ordering,
  timestamps and bounded allocation. The pending-resize assertion also passed.
- NVIDIA AV1/HEVC at 120 FPS and H.264 at 60 FPS, mixed audio and silent: six
  actual recordings passed elementary-stream BT.709 limited-range checks and
  flat color-patch comparison with the NDI-derived input (maximum measured
  Y/U/V code difference: zero). The decoded frame-ID and synthetic AAC
  continuity assertions also passed on all six hardware cases, with no repeats.
- Cloud regression checks passed on commit 48b452a for folder and portable
  builds. Later native-input scaling changes need a fresh CI run.

The installed copy's current replay was saved before pausing its replay and
NDI through the existing private Stream Deck API. This prevents a second active
encoder from confounding the tests. Restore the saved replay/streaming flags
from the ignored local investigation state before normal operation/shutdown.
NVIDIA Overlay was initially disabled and was enabled for comparison; restore
that preference after testing. Do not change privacy/security settings.
- Visible-window comparisons were affected by foreground-game/Steam coverage.
  Two later runs also accidentally used the CLI defaults of 60 FPS/20 seconds
  because `--fps120`/`--seconds12` lacked argument separators. Do not cite
  those as 120 FPS benchmarks. The diagnostic now requires marker coverage
  throughout the export; correct usage is `--fps 120 --seconds 12`.

## Exploratory game capture, before the testing pause

Two sequential 12-second captures of the current game at an explicitly checked
120 FPS used production capture/recording, HEVC and silent audio, with no
windows or input injection. The installed app remained running. The scene
was not fixed and the runs were not simultaneous; these observations are not
a controlled speedup or an NVIDIA App comparison.

| Observation | 0.7.27 baseline | Candidate |
| --- | ---: | ---: |
| Captured images per second | 90.33 | 101.13 |
| Mean time inside capture call | 3.79 ms | 1.17 ms |
| Median delivery age | 7.46 ms | 12.21 ms |
| Repeated recording inputs / encoded frames | 450 / 1498 | 239 / 1497 |
| Source-history misses | 0 | 0 |
| Encoder sessions | 1 | 1 |

Source availability still limits distinct recorded images. These results do
not establish stable 120 distinct FPS, zero impact on game FPS or perceptual
smoothness. Reports and clips are kept in ignored local investigation folders;
do not upload the user's gameplay or audio to CI/public release artifacts.

## Required follow-up before publication

1. Run cloud regression checks on folder and portable builds, including the
   updated SDR/frame-ID/AAC tests. Keep visible diagnostics out of CI.
2. On the available gaming PC, test 1440p 60/120 FPS with AV1 and HEVC, mixed
   and separate local/KVM audio, HDR enabled and SDR output. Use matched
   changing-source runs, decode frame IDs and check actual source gaps versus
   added repeat/drop pairs. Measure p95/p99 timing and resource/game-FPS impact.
3. Compare NVIDIA App and DeskGlide on a repeatable scene with the same game
   FPS cap, output FPS, resolution and codec/bitrate where available. Measure
   source limitations separately. Do not promise proprietary-pipeline parity
   from FPS metadata or encode throughput alone.
4. Verify long recordings, consecutive saves, app switches and CS2 attribution,
   plus audio continuity/A/V drift and NDI delivery latency after queued readback.
5. Update these findings and release notes to reflect the final measured
   behavior, then publish through the existing signed release workflow. Keep
   the current key. Do not restart the user's PCs during a game or shut down
   the PC based on the old, already completed shutdown request.
