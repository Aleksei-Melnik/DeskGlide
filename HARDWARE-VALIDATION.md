# DeskGlide hardware validation

## Recording cadence, scaling and SDR — 0.7.28, 2026-10-18

The RTX 5070 matrix covers all 24 UI codec/resolution/frame-rate combinations:
AV1/HEVC/H.264, 720p/1080p/1440p/4K and 60/120 FPS. Decoded source-frame IDs,
three consecutive saves, mixed/separate AAC and elementary-stream SDR headers
passed. Additional native/CUDA-scaled color checks matched flat NDI-derived
patches. See [the detailed recording report](docs/RECORDING-VALIDATION-0.7.28.md)
for long-run/audio/NDI results, the limited NVIDIA App comparison and remaining
desktop-source repetitions at 120 FPS. The older hardware coverage statements
below describe their dated releases rather than the current matrix.

## Replay memory optimization — 0.7.23, 2026-10-13

The raw video encoder is independent of the delayed WASAPI/KVM audio input.
It writes compressed, timestamped NUT packets to the segment muxer, which
copies video and encodes audio. This keeps one hardware video encoder. The
raw input queue is eight frames; the muxer queue holds compressed packets.
Video retains a 100-ms capture-jitter allowance; audio keeps the existing
300-ms local / 500-ms KVM allowance and shared epoch/sample timestamps.
Unused RGB16/P010 GPU readback resources are initialized on demand.

Both builds recorded the same changing 2560 × 1440 GPU fixture at AV1 Ultra,
120 FPS, while the installed utility continued recording/transmitting.
Windows performance counters sampled private working set every two seconds;
the table uses the matching 15–43-second steady interval and excludes export
processes. All recorder-owned encoding/mux processes are included.

| Private working set, average | Before | 0.7.23 |
| --- | ---: | ---: |
| DeskGlide fixture | 278.3 MiB | 134.2 MiB |
| Hardware encoder FFmpeg | 246.8 MiB | 245.9 MiB |
| Video-copy/audio mux FFmpeg | — | 13.2 MiB |
| **Combined** | **525.1 MiB** | **393.3 MiB** |

The final AV1 run lasted 90 seconds before export, with one encoder session,
zero source-history misses and zero missing local sample positions out of
5,228,800 read. Raw history plus its reuse cache sampled about 69–79 MiB.
The fixture intentionally keeps an independent PCM reference in memory;
its growth is test overhead, not part of the production replay recorder.
This comparison is replay-only: NDI receivers, remote viewers, UI and native
drivers can increase full-app memory. It does not establish a long-term memory
ceiling or a zero-overhead game-FPS result.

Three saves were decoded and checked against source frame IDs: no available
source frame was lost and save boundaries had no overlap/gap. The producer
itself skipped frames during scheduling/GPU delays, so 120 output FPS does
not establish 120 distinct captured images. RMS correlation with independently
timestamped WASAPI audio measured about 3–10 ms A/V offset without accumulating
drift or low-energy holes. A pure 440-Hz-only phase assertion flagged the
mixed desktop sound in both builds; the same phase changes existed in the
independent reference. Separate synthetic tone checks verify continuous audio
for the mixed and three-track exports without that background ambiguity.

NVIDIA cadence checks exercised AV1/HEVC/H.264 at 120 FPS, mixed/separate audio,
the 500-ms network-audio allowance with synthetic data, and silent HEVC at
60 FPS. The network-audio fixture does not establish actual LAN packet delivery.
Local AMD H.264/HEVC runs exercise the AMF path at 60 FPS. Physical Intel,
other GPU generations and 4K/8K recording remain outside this verification.
Shutdown/reconfiguration checks stop both recording processes; no global
keyboard/mouse input is injected, and no installed profile is changed.

## Freeze, memory and AV1 playback investigation — 0.7.21, 2026-10-12

The installed 0.7.19 process stopped responding during its updater exit
handshake. Managed stacks showed the UI waiting in StreamEngine.Dispose while
the sender worker was inside native NdiSender.Dispose. The old process was
recovered for the user's Discord call; all subsequent work used isolated
folders. No global keyboard/mouse input was injected, no installed profile was
changed, and no additional synthetic saturation workload was run while the
user played Phasmophobia.

The production NDI exit path now has a bounded drain. Timed-out workers retain
their resources until completion; global NDI teardown is skipped if an owning
worker remains inside the runtime. Tests cover blocked/completed/faulted workers,
deferred cleanup, rejection of live duplicate instances and recovery of an
existing unowned mutex. These tests exercise the deadline policy; they cannot
guarantee that the vendor's native destroy call will return.

The original Phasmophobia clip contains AV1 2560 × 1440 at 120 FPS, with
13,840 video packets over 115.333 seconds. Video/audio packet timelines are
continuous and nondecreasing. Continuous mixed audio does not establish that
every individual audio input remained audible or prove perceptual A/V sync.

Both playback PCs had MPC-HC 1.8.8 and LAV 0.74.1.24. A process-local DirectShow
graph using the registered LAV splitter/decoder and a frame-counting null
renderer decoded 1,201 frames (10 seconds) in 21.227 seconds: 56.58 FPS. It had
no display, audio renderer or reference-clock pacing. The same 10-second footage
transcoded to HEVC decoded at 286.66 FPS in that graph, and the user confirmed
normal-speed playback in MPC-HC. Modern FFmpeg D3D11 decoded the original AV1
at approximately 229 FPS, including GPU-to-CPU transfer. LAV's official 0.75
release introduced AV1 hardware decoding; 0.74 does not support it. This explains
the reproduced slow playback without changing the source file's timestamps.

Replay memory experiments first demonstrated that compact managed buffers
alone still left over 1 GiB committed through LOH/ArrayPool retention. Replay
now owns exact-sized native NV12 slabs, with at most 32 MiB/four free slabs;
NDI's private cross-thread pool retains at most 64 MiB/eight buffers. Live
recording history is separate from these free-cache limits.

A final isolated replay-only process recorded the user's actual game for
90 seconds at AV1 High/native 1440p120, alongside the installed recorder. Its
working set sampled every five seconds stayed 221,917,184–297,238,528 bytes
(approximately 212–284 MiB), with no continuing growth. Managed memory stayed
around 40 MiB. The 88.810-second export used one encoder session; 4,892,800
local audio sample positions had zero missing samples, and source history never
exhausted. The source supplied 5,859 acquired frames (about 65/s) and the recorder
repeated the latest source image as necessary on its 120 FPS timeline. These
results do not establish 120 distinct game frames per second or an NVIDIA App
performance comparison. Full-app memory with NDI/remote viewing enabled may be
higher; physical Intel, other GPU generations and perceptual full-source audio
sync remain outside this measurement.

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
