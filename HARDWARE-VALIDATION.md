# DeskGlide hardware validation — 0.7.17, 2026-10-11

## Tray interaction update — 0.7.18, 2026-10-12

The tray-only update adds a single left click for Settings, creates the menu
before publishing the icon and enables WinForms message filtering in WPF from
startup. The isolated Windows CI posts native callbacks to the test NotifyIcon,
reproduces the previous empty-menu first-opening cancellation, then verifies
first and repeated menu opening, Settings actions, Escape dismissal and no
extra Settings request from the double-click notification. Folder and portable
builds run this suite alongside the existing WPF window tests. It uses no global
keyboard/mouse injection. These checks do not replace a Windows boot/login test
on every user's installation. The capture/recording implementation is unchanged
from the measurements below.

Windows 11, Ryzen 7 7800X3D, RTX 5070 and integrated AMD Radeon, 32 GB RAM.
The installed 0.7.16 utility and its profile were preserved. Its HEVC 120 FPS
recorder continued running alongside the isolated tests. No input was injected
into the user's desktop or other PCs. Diagnostic clips stayed in test folders;
they were not delivered to the user's recording share.

## Replay under load

The moving 2560 × 1440 fixture renders a changing, textured GPU scene with
visible frame IDs, captures real WASAPI loopback audio from RODECaster Duo,
and records through the production replay pipeline at Ultra quality. Fourteen
CPU workers produce approximately 100% CPU load; a separate D3D compute load
keeps the RTX GPU near saturation. Saved clips are decoded and checked for:

- every source frame available at each output timestamp;
- exact frame count and consecutive save boundaries without overlap or gaps;
- audio holes and discontinuities in a continuous tone;
- start/end A/V timing against a separate timestamped loopback reference;
- one encoder session, no recovery and no exhausted source history.

A diagnostic build of the previous 0.7.16 recorder reproduced the failure at
AV1 120 FPS: 861 source-history misses, 400 repeated input frames and two encoder
starts under load. The new direct NV12 path, dedicated pipe writers and recording
clock address the measured scheduling and backlog failures. Original gameplay
clips had continuous packet timestamps and decoded without corruption; those
facts alone do not establish smooth capture or isolate every original symptom.

The final NVIDIA matrix ran each codec/FPS pair for 30 seconds under load and
decoded three consecutive saves. All six passed with no repeated/skipped frames
in the saved intervals, no audio holes or phase discontinuities, no save overlap
or gaps, and no encoder restarts. A/V offset was within 19 ms without drift.

| Backend | Codec | FPS | Available source frames lost | Audio holes |
| --- | --- | --- | --- | --- |
| NVIDIA NVENC | AV1 | 60 | 0 | 0 |
| NVIDIA NVENC | AV1 | 120 | 0 | 0 |
| NVIDIA NVENC | HEVC | 60 | 0 | 0 |
| NVIDIA NVENC | HEVC | 120 | 0 | 0 |
| NVIDIA NVENC | H.264 | 60 | 0 | 0 |
| NVIDIA NVENC | H.264 | 120 | 0 | 0 |
| AMD AMF | H.264 | 60 | 0 | 0 |
| AMD AMF | H.264 | 120 | 0 | 0 |
| AMD AMF | HEVC | 60 | 0 | 0 |
| AMD AMF | HEVC | 120 | 0 | 0 |

The four AMD runs explicitly selected the integrated Radeon encoder while the
same RTX-rendered scene and stress load ran. They passed continuity, save
boundaries and A/V checks without encoder recovery. At H.264 120 FPS, the scene
producer missed 23 scheduled inputs in the saved intervals; all available input
frames were retained. This integrated Radeon does not support AV1 encoding.
Intel fallback selection is covered by deterministic tests; no Intel GPU is
present for physical validation.

The final **180-second HDR AV1 1440p120** test passed all decoded checks. Its
179.917-second first clip contained 21,590 frames; the next two clips started on
the immediately following frame. There were zero losses of available source
frames, zero history misses, one encoder session and zero missing samples across
13,811,200 recorded stereo sample positions. Measured A/V offsets were within
6 ms, with no accumulating drift within each clip.

Under saturation, the GPU scene producer itself occasionally did not create a
scheduled source frame. The first long clip had one such held frame, and the
second had six. The recorder preserved all available frames. This distinction
prevents equating a configured/output 120 FPS with 120 distinct source images
when the source itself is slower.

## Save, media conversion and packaging

- Failed exports retain unsaved footage. Repeat saves contain only the interval
  since the last successful Save, capped by the configured buffer duration.
  Recording-format changes can discard the unsaved buffer, as before.
- Recording reconfiguration, disable and disposal produced no unsolicited clip.
  Mixed audio, three separate tracks, silent recording and failed
  network-delivery retention passed. HEVC three-track and AV1 silent fixtures
  each decoded 1,200 distinct frames at 120 FPS without repeats or skips.
- A 35-second real HDR desktop capture at native 1440p120 completed with one
  encoder session and no error. Rebuilding desktop capture at 20 seconds did
  not restart recording. Both microphone and PC loopback endpoints were active.
  The desktop was largely static: this validates capture recovery and the
  output format, rather than 120 distinct desktop updates per second.
- Byte-exact pipe transfer, blocked-write cancellation, client disconnect and
  concurrent server disposal passed. Shutdown completed within one second.
- NV12 conversion preserved every luma code and the expected chroma averages,
  including scalar tails. The 1440p conversion benchmark averaged about 0.4 ms
  without stress; it transfers 25% fewer video bytes than the previous UYVY path.
- The release's portable executable ran from a directory containing only that
  EXE. Its bundled dependencies, update/rollback/restart checks, audio timing,
  GPU-backend selection, media conversion and pipe tests passed. Release hashes
  and the RSA update signature matched the embedded public key.
- The exact portable release EXE additionally passed an AV1 1440p120 hardware
  stress run with three decoded saves: 3,115 frames, no repeats/skips, no audio
  discontinuities or missing sample positions, and no encoder restart.
- GitHub's Windows CI passed the folder and portable suites, including quality
  labels at 1440p120/1080p60 and live English/Russian selection preservation.

## Scope

The stress fixture tests recording under artificial resource pressure, rather
than measuring game FPS against NVIDIA App. Zero performance impact, every GPU
generation, physical Intel support, 4K/8K throughput and multi-hour buffering
are not established. NDI, KVM and cross-PC audio keep their existing transport
paths; their earlier local measurements below are not a new remote-LAN test.

## Earlier 0.7.16 validation — 2026-10-09

Windows gaming PC: Ryzen 7 7800X3D, RTX 5070 (driver 616.64), integrated
AMD Radeon, 32 GB RAM, HDR desktop at 2560 × 1440. The installed utility
and its settings remained unchanged. Tests used isolated local buffer folders;
no clips were delivered to the user's recording folder. No keyboard/mouse input
was injected into connected computers.

## GPU recording

Moving-frame replay fixtures encoded identifiable frame numbers and checked
the decoded result, rather than trusting the output file's configured FPS.

| Backend | Codec | FPS | Audio | Repeats / skips |
| --- | --- | --- | --- | --- |
| NVIDIA NVENC | H.264 | 60 | Mixed | 0 / 0 |
| NVIDIA NVENC | HEVC | 60 | Mixed | 0 / 0 |
| NVIDIA NVENC | HEVC | 120 | Three tracks | 0 / 0 |
| NVIDIA NVENC | AV1 | 120 | Silent | 0 / 0 |
| AMD AMF | H.264 | 60 | Mixed | 0 / 0 |
| AMD AMF | HEVC | 60 | Three tracks | 0 / 0 |
| AMD AMF | HEVC | 120 | Silent | 0 / 0 |

Each fixture ran for approximately 12.5 seconds and exported the rolling window.
AMD runs explicitly selected the AMF backend in the isolated diagnostic process
using the production encoder profiles; the normal application still chooses
NVENC first when both GPUs are present. Two-frame preflight additionally passed
NVIDIA H.264/HEVC/AV1 and AMD H.264/HEVC at both 60 and 120 FPS. This integrated
Radeon rejected AV1; no Intel GPU was installed, so Quick Sync was unavailable
as expected.

A separate 180-second HDR desktop capture/replay run at native 1440p60 completed
with one encoder start, no recording error, and a decodable 179.09-second export.
Disabling and re-enabling NDI did not stop replay. Rebuilding desktop capture
did not restart the encoder. Background recording, quality changes, disable
and shutdown produced no export unless the test explicitly requested Save.

## NDI, audio and KVM

- A 90-second local NDI round trip delivered native 1440p SDR at about 60 Hz.
  There were no receive timeouts; the largest receive gap was 21.9 ms. Desktop
  content was largely static: this measures delivery cadence, not 60 distinct
  source images per second or performance inside a running game.
- The existing NDI 5 Tools runtime was reused without launching an installer.
  The official NDI 6.3.2 download passed its pinned SHA-256 and Windows signature
  checks. Dependency tests cover cancelled/manual retry, concurrent callers,
  updated runtime paths and invalid/oversized cached installers.
- A ten-second real WASAPI loopback test on RODECaster Duo captured all 480,000
  expected sample positions with zero late samples at the recording deadline.
  Diagnostic playback was a quiet tone; raw audio was not saved.
- Authenticated local KVM video measured 30.02 and 60.02 FPS at 1440p. No remote
  input was injected. This is a loopback transport test, not a remote LAN test.

## Limits

Physical Intel recording, other AMD/NVIDIA generations, interactive first-time
NDI installation, 4K/8K throughput, multi-hour buffering and sustained GPU-heavy
gameplay are not established by these tests. Codec, resolution and FPS support
still depend on the GPU and driver. Fullscreen gameplay, cross-PC input and
Discord's end-to-end video/audio behavior need their own live measurements.
