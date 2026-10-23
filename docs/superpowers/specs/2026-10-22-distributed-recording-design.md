# DeskGlide recording design

User authority: the supplied 2026-10-22 task explicitly requests architecture analysis,
plan, implementation and available testing without stopping at recommendations.
Work in the user's existing DeskGlide checkout on codex/distributed-recording.

## Existing system and constraints
StreamEngine owns DXGI acquisition and publishes immutable SDR UYVY VideoBuffer
references to NDI and ReplayRecorder. ReplayRecorder converts to NV12, preflights
real FFmpeg hardware encoders, aligns video/audio clocks, writes independent
2-second Matroska segments and exports lossless MP4 via ReplayDelivery.
NDI currently sends only 60 FPS. KVM supplies a paired TLS control channel.
There is no independent continuous recording or network recording receiver.
CleanOldBuffers currently assumes only one recorder; concurrent instances require
an ownership lock and protection for unfinished continuous recordings.

## Decisions
1. Keep the existing capture/color/encoding path. One local desktop duplication
feeds all consumers. One hardware encoder serves replay and continuous recording
when all encoding/audio/source settings agree; different settings may need two
encoders, but not two desktop captures.
2. Continuous recording pins completed segments between Start and Stop. Preserve
a journal and completed Matroska files if export/encoder/device failure occurs.
Never delete an unfinished recording as old ring-buffer data. Use the existing
atomic MP4 publication and retryable delivery mechanism. Do not overwrite files.
3. Remote mode uses the existing compressed NDI High Bandwidth transport, with
explicit 60/120-FPS output and a common video/audio source clock. Receive UYVY
without a BGRA round trip. A bounded receiver offers frames to the same recorder;
a shared timestamp mapper positions 48-kHz NDI audio on the video clock. A stalled
source must pause/finalize rather than record an indefinitely frozen frame.
4. Alternatives considered: raw UYVY is approximately 3.54 Gbit/s for 2560x1440@60
and 7.08 Gbit/s at 120 before overhead, so it is not a universal LAN solution.
A separate low-latency H.264/HEVC transport would consume a gaming-PC encoder;
retain it as an evidence-driven alternative if NDI cannot meet the measured LAN
budget. Capture-card ingestion is conditional on hardware availability; do not
claim one is present or tested. The local physical link reports 5 Gbit/s; this
does not establish end-to-end throughput to the other PC.
5. Separate paired recording RPC from Stream Deck's host-only command protocol.
Commands are allowlisted, bounded and idempotent by session/request ID; never
accept arbitrary destination paths, shell arguments or unauthenticated peers.
The receiving PC controls its own output folder and permits remote encoding in
settings. Reconnect can restore replay configuration; uncertain start/stop must
be reconciled by status, never blindly toggled again.
6. Actual encode probes determine supported codec/resolution/FPS on the selected
computer. No GPU-model hardcoding. Expose quality/VBR/CBR, bitrate, preset and
keyframe interval with backwards-compatible defaults. Keep SDR BT.709 tags.
7. Add independent continuous-recording preferences, shortcut and start/stop
controls. Replay and recording can each select this PC or a paired encoding PC;
in remote mode the selected node records this PC's NDI stream. Show offline,
unsupported codec, source loss, buffer/recording status and performance counters.
8. Screenshot panels remain one compact bottom-right L-shaped cluster. At a
full-screen crop they move inside that same corner, without flipping top/left.

## Verification and claims
First reproduce each missing behavior with failing tests. Cover simultaneous
recording/replay, ring eviction with active pins, recorder ownership, failed
export recovery, capability filtering, duplicate RPC, disconnect and shared A/V
clock. Exercise real FFmpeg files with synthetic video/audio, decode/probe them,
and collect durations/frame IDs/codec/color/audio/CPU/memory/network metrics.
Run all existing CI checks for folder, portable and signed packages. No focus or
input takeover during a game. Separate local/loopback evidence from two-PC GPU,
LAN, sustained gaming-FPS and capture-card measurements that are not available.
