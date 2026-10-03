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

For Discord without OBS, run NDI Webcam Input on the **receiving/streaming PC**,
choose this NDI source, and select its virtual video and audio devices in Discord.
ScreenCapture does not install a virtual camera or audio driver itself.

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

Requires Windows x64, PowerShell 7 and .NET 10 SDK:

```powershell
./Build.ps1
./app/ScreenCapture.exe --update-tests
./app/ScreenCapture.exe --audio-timing-test
./app/ScreenCapture.exe --kvm-test
```

Tests write JSON reports beside the executable. `--ndi-audio-test` also needs
the NDI runtime and checks a synthetic signal at 100%, 50% and mute. Hardware
capture/encoding tests require a real desktop and GPU and are not run in CI.

To package a release, set the version in `src/ScreenCapture.csproj` and run
`scripts/New-Release.ps1 -SigningKey <private-key-outside-repo>`. Use
`scripts/Publish-Release.ps1 -ReleaseFolder <generated-folder> -NotesFile <file>`
with a GitHub token in the environment or Git Credential Manager. It creates a
draft, uploads all three assets and publishes only after successful uploads.
Forks must change `Updates.Repository` and the embedded public key together.

Source: MIT. Dependencies retain their own licenses; see THIRD-PARTY-NOTICES.md.

