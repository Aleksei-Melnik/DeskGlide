# DeskGlide distributed recording implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline, task by task.

**Goal:** Local and paired-PC replay plus independent continuous recording with
hardware capability selection, preserving the existing capture pipeline.
**Architecture:** Extend segment recording with pinned continuous leases; route a
single capture to sessions; reuse NDI for network video/audio and KVM TLS for
bounded recording control. Keep UI and journal/recovery separate from transport.
**Tech Stack:** .NET 10 Windows, WinForms/WPF, DXGI/D3D11, FFmpeg/NVENC/AMF/QSV,
NDI runtime, existing KVM TLS and System.Text.Json.
**Spec:** ../specs/2026-10-22-distributed-recording-design.md

## Global constraints
- Existing settings, replay/audio/color, KVM and signed-update trust stay compatible.
- No private signing material in source, logs, tests or public assets.
- Temporary files under AppData/Temp or ignored work/, never the root of C:.
- No interaction with an active game or a user's clipboard during background tests.
- Real hardware/LAN evidence must be distinguished from synthetic/loopback checks.

## Review focus
- Starting a second recorder must not clean the first recorder's live segments.
- An active recording must survive replay eviction and an export failure.
- Changing format while recording must not concatenate incompatible streams.
- Network duplicate requests/disconnect must not toggle or start twice.
- Source/audio timestamps and bounded queues must survive source loss/reconnect.

## Tasks

### 1. Screenshot anchor
Files: src/ScreenshotToolbar.cs, src/ScreenshotTests.cs.
- [x] Reproduce full-screen controls flipping to top/left with a failing test.
- [x] Keep both bars at bottom-right; clamp inward and retain non-overlap.
- [x] Run screenshot and 1440p damage/DPI tests; inspect edge previews; commit.

### 2. Continuous segments and ownership
Files: ReplayRecorder.cs, new ContinuousRecording.cs and RecordingSessionTests.cs.
Interfaces: BeginRecording(ReplayOptions destination,string app) returns a lease;
StopRecordingAsync(lease) returns ReplayResult; unfinished journals remain recoverable.
- [x] Add failing real-segment tests for start/stop, pinning, concurrent owners,
  export failure/recovery and shared replay saves.
- [x] Add lifetime ownership locks, journaling, pin registration and extraction
  of the existing MP4 export into a shared interval exporter.
- [x] Add RecordingCoordinator sharing equal profiles and a second encode only
  for distinct profiles; reject incompatible changes during a recording.
- [x] Decode generated files and verify duration, frame order, audio and colors.

### 3. Hardware capabilities and encoding preferences
Files: ReplayOptions.cs, RecordingEncoder.cs, new RecordingCapabilities.cs/tests.
Interfaces: supported codec list from real SelectAsync probes, encoding profile
identity independent of save folder/hotkey; defaults retain the old p4/quality path.
- [x] Tests first for old settings, invalid values and emitted rate-control/GOP
  arguments; actual codec support determined by probe success.
- [x] Implement capability caching and supported UI choices; add GPU/CPU/memory,
  frame-drop/repeat and latency diagnostics without expensive per-frame polling.

### 4. Network input and clock
Files: NdiTransport.cs, StreamEngine.cs, new RecordingNdiSource.cs/tests.
Interfaces: disposable NDI source publishes VideoBuffer and timestamped stereo
48-kHz PCM; one common source-to-local clock; no unbounded video/audio FIFO.
- [x] Tests first for time mapping, native UYVY validation, pacing, source loss
  and audio alignment; extend sender frame rate/timecode with default 60 FPS.
- [x] Implement receiver reconnect and stale-source handling; use existing
  conversion/encoder rather than recapturing a remote viewer window.
- [x] Loopback synthetic A/V test with measured received rate/gaps/duration.

### 5. Paired orchestration
Files: KvmService.cs, new RecordingRemote.cs, TrayRecording.cs and tests.
Interfaces: bounded allowlisted status/capabilities/configure/start/stop/save
commands over paired control; receiver owns paths; request/session IDs deduplicate.
- [x] Tests first for unauthorized/malformed/duplicate/conflicting requests,
  timeout reconciliation, disconnect and replay restoration.
- [x] Connect local/remote session selection, NDI demand/rate and save shortcut.

### 6. Settings and controls
Files: TrayApp.cs, TrayStreamDeck.cs, Ui/SettingsWindow.cs (split recording page
into partial file), ConfigurationBackup.cs, Strings.ru.json, UI/feature tests.
- [x] Add independent recording preferences, folder, shortcut, start/stop action,
  selected encoding PC and receiver opt-in, codec capability refresh and status.
- [x] Preserve older settings/profile backups, replay and Stream Deck behavior.
- [x] Native UI tests on CI; no active-desktop takeover locally.

### 7. Verification, review and release
- [ ] Run targeted and complete folder/portable/signed CI suites and assess review.
- [x] Hardware codec checks and measured synthetic capture; report unavailable
  two-PC/LAN/long-game/card checks precisely rather than claiming no lag.
- [ ] Update docs/release notes, publish signed next version with the existing
  key, verify downloaded payload and old-version update discovery.

CPU x264 was added to both profiles by user request, with strict H.264 backend validation, actual preflights and Quality/VBR/CBR media tests. Stream Deck 1.1.0 adds recording Start/Stop and state feedback.

Physical two-PC/RTX 3080, capture-card hardware and hour-long game benchmarks are unavailable; validation uses real local RTX 5070 probes and native NDI synthetic A/V loopback. No zero-FPS-impact claim.
