# ScreenCapture

Windows 11 x64 tray application for a gaming PC and a streaming PC. Captures
the desktop, converts it to SDR and sends it to OBS/DistroAV over NDI High
Bandwidth. Includes GPU instant replay, audio capture and paired-PC controls.

**Download:** [latest release](https://github.com/Aleksei-Melnik/ScreenCapture/releases/latest).

## First run

1. Extract the ZIP into a writable local folder and run `ScreenCapture.exe`.
2. Install [NDI Tools / Runtime](https://ndi.video/tools/) for NDI functionality.
   The proprietary NDI runtime is not included in this repository or release.
3. For recording, run `Install-RecordingTools.ps1` beside the executable. It
   downloads the pinned FFmpeg build from its upstream distributor and checks
   SHA-256. Alternatively put `ffmpeg.exe` and `ffprobe.exe` in `tools`.
4. Open Settings from the tray and select the monitor, NDI audio endpoint,
   recording folder and replay audio inputs. Local paths and UNC network paths
   are supported; replay buffering happens locally before background delivery.
5. If Windows blocks incoming LAN connections, run `Enable-Lan.ps1` as
   administrator. It permits this executable only on wired private local networks.

For upgrading 0.5.x, exit the app and extract this release **over its existing
app folder**, keeping `tools`. `SdrCapture.exe` is a compatibility launcher for
existing shortcuts, Stream Deck commands and firewall rules; both launchers run
the same ScreenCapture application. Existing settings stay in
`%LOCALAPPDATA%\SdrCapture`, and the NDI source name remains `SdrCapture SDR`.

## NDI and Discord

Enable NDI in the tray. In OBS/DistroAV select `PC-NAME (SdrCapture SDR)`, BT.709,
Limited range, Highest bandwidth and Low latency. Leave OBS output in SDR Rec.709.
NDI transmission uses High Bandwidth, not HX/HEVC. Recording has its own hardware
encoder and quality settings.

Settings → NDI has an audio device selector and a **0–100% volume slider**.
The default is 50%; 0% mutes outgoing NDI audio. This slider does not change
the recording tracks or Windows device volume. Select Silent for no audio.

For Discord without OBS or NDI Webcam Input, open **Discord · приём** on the
**receiving/streaming PC**. Install **ScreenCapture Camera**, find/select the
gaming PC's NDI source, enable reception and save. The native camera offers
1920×1080 RGB24/RGB32 video at 60 FPS, with 30 FPS negotiation for clients that
initialise preview at a lower frame rate. RGB24 is first because native WebRTC
DirectShow capture ignores RGB32. It is registered for the current Windows user;
restart Discord after first installation. Select it under screen share → Devices
and select 60 FPS in Discord. Discord's account, streaming settings and network
can still limit the outgoing stream; the device cannot override those limits.

For sound, use **Установить аудиокабель…** on that same receiving PC. This downloads
the pinned, signed, original **VB-CABLE** package and opens its administrator
installer. Click Install Driver and reboot if requested. Select the cable output
in ScreenCapture and use **Связать устройства**. The recording endpoint becomes
**ScreenCapture Camera Audio (VB-CABLE)**, matching the complete camera name.
The name can be customised in settings. Both endpoint IDs and the vendor's
interface name remain unchanged. Fully quit/reopen Discord after updating.
Chromium can infer the camera/audio group from these names; Discord's saved
device selection may still require choosing the paired audio input once.
VB-CABLE is donationware by VB-Audio; licensing and
donation links are in our settings. Existing cables are reused. ScreenCapture
snapshots the six Windows audio-default roles before installation and restores
the original active endpoints if the installer selects the new cable instead.
Recovery is retained for the installer's reboot; a deliberate switch to another
non-cable device is preserved. Previous defaults from installations before 0.7.1
cannot be reconstructed: restore those once in Windows sound settings if needed.
Audio can be Silent and has a separate receiver volume slider.

No cable or camera is installed automatically, through updates or through KVM
pairing. Installation is blocked on the controlling/gaming PC. Camera binaries
are kept in a versioned user directory so a running Discord cannot block updates.

## Replay and paired PCs

- Replay can be enabled independently of NDI. Duration: 5–20 minutes.
- GPU recording: HEVC, H.264 or AV1 when supported by the installed GPU/FFmpeg.
- Recording at 60 or 120 FPS, selectable quality, configurable save hotkey.
- Game audio, microphone and optional remote-PC audio; Silent per input;
  one mixed track or separate tracks. Clips are grouped by foreground game/app.
- KVM roles: controlling PC and controlled PC, paired with a generated code.
  Arrange individual monitors in Settings, including two monitors of the same
  remote PC on opposite sides of the local monitor. Clipboard, file transfer,
  optional remote audio for replay and remote viewing are included.

Tray → **KVM · управление компьютерами…** opens an independent computer chooser.
`ScreenCapture.exe --kvm` can be assigned to a shortcut or Stream Deck button.
Each remote viewer has monitor selection, fullscreen (F11), a view-only toggle
and Disconnect; closing it does not stop capture or recording. Reopening the
same PC activates its existing window. Settings have contextual descriptions,
role-specific fields and a short transition that follows Windows animation settings.

In a connected computer's KVM card, enable **Только окно KVM** to exclude it
from physical monitor transitions while keeping remote viewing/control available.
Alternatively, enable **Режим сервера** on the controlled computer. Excluding a
PC retains its saved monitor positions; disabling the option restores them.
There is no timed cooldown when crossing monitor edges. Actual input latency
still depends on Windows scheduling and the network.

The viewer defaults to native-resolution PNG for sharp text, with selectable
high-quality JPEG and bandwidth-saving 1080p. Large frames fall back to bounded
JPEG rather than exceeding the transport's packet limit.

File dragging is an unfinished experiment and has been reported not to hand off
between the actual PCs. Do not rely on it in this release. Use **Ctrl+C / Ctrl+V**
for file transfer in either direction. The current experimental implementation
only attempts controlling-PC → controlled-PC handoff; reverse drag is not
implemented. This update does not fix drag/drop.

**Мой профиль** exports an encrypted `.scprofile` for each PC, including pairing
identity, monitor layout, devices, replay/NDI/Discord settings and autorun. Keep
the password. Restore that PC's own profile after reinstalling Windows; retain
the computer name and reselect audio endpoints if Windows changed their IDs.
Virtual devices still need installation on a fresh receiving PC.

KVM and remote audio need the program on both computers. Secure desktops/UAC,
headless machines without a usable capture surface, elevated windows, games and
anti-cheat can restrict capture/input. The experimental KVM path has local
protocol tests; broad real multi-PC validation is still pending. Frame rate and
latency depend on GPU load, display mode and LAN performance; zero latency is
not promised.

## Updates

Tray → **Обновления…** → **Обновить все подключённые ПК** on the controlling PC.
Each PC downloads the same release and checks its RSA signature and file hashes.
Only then does it save its replay, restart and retain settings/pairing/recordings.
The host waits for clients to reconnect before updating itself. Failed file
replacement or startup triggers restoration of the previous app files.

Every PC needs a **one-time manual upgrade to 0.6.0**; older versions have no
update command. Offline PCs must reconnect and be updated later. A controlled
PC can disable updates from its host in Settings → Updates. Startup checking
only notifies; it does not automatically install updates.

The release manifest is signed with the maintainer's release key. This is
application-level update verification, **not Windows Authenticode signing**.
The private key is never stored in the repository, build artifacts or app.
GitHub access requires an internet connection; no GitHub login is needed to update.

## Build and test

Requires Windows x64, PowerShell 7, .NET 10 SDK and Visual Studio C++ desktop
build tools / Windows SDK:

```powershell
./Build.ps1
./app/ScreenCapture.exe --update-tests
./app/ScreenCapture.exe --audio-timing-test
./app/ScreenCapture.exe --kvm-test
./app/ScreenCapture.exe --ui-tests
./app/ScreenCapture.exe --feature-tests
./scripts/Build-Camera.ps1 -Output ./app/camera -Probe
./app/ScreenCapture.exe --camera-test
./app/ScreenCapture.exe --camera-ndi-test
./app/ScreenCapture.exe --discord-device-test
```

Tests write JSON reports beside the executable. `--ndi-audio-test` also needs
the NDI runtime and checks a synthetic signal at 100%, 50% and mute. Hardware
capture/encoding tests require a real desktop and GPU and are not run in CI.
The camera probe loads the filter directly without installing devices or drivers.
It verifies real DirectShow RGB24/RGB32 samples, 1080p60 timestamps, 30 FPS preview,
connected-graph renegotiation, orientation, colour channels and changing image data.
The NDI camera test uses a synthetic 2560×1440 source and also needs the NDI runtime.
The device test checks default-restore rules and reads Windows PolicyConfig without
changing audio devices. Actual Discord publishing and VB-CABLE playback still
require a receiving-PC test. Bounded native camera logs under
`%LOCALAPPDATA%\SdrCapture\Discord\camera-*.log` record formats and frame counters,
never image or audio content.

Compatibility references: [WebRTC DirectShow formats](https://github.com/webrtc-mirror/webrtc/blob/main/modules/video_capture/windows/device_info_ds.cc)
and [Chromium GuessVideoGroupID](https://github.com/chromium/chromium/blob/main/content/browser/renderer_host/media/media_devices_manager.cc).

To package a release, set the version in `src/ScreenCapture.csproj` and run
`scripts/New-Release.ps1 -SigningKey <private-key-outside-repo>`. Use
`scripts/Publish-Release.ps1 -ReleaseFolder <generated-folder> -NotesFile <file>`
with a GitHub token in the environment or Git Credential Manager. It creates a
draft, uploads all three assets and publishes only after successful uploads.
Forks must change `Updates.Repository` and the embedded public key together.

Source: MIT. Dependencies retain their own licenses; see THIRD-PARTY-NOTICES.md.

