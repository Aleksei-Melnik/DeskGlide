# DeskGlide hardware validation — 2026-10-09

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
