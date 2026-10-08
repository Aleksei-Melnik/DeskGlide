# ScreenCapture

Screen streaming, instant replay and KVM for Windows 11. ScreenCapture is a
standalone NeuralMea utility. Its interface supports English and Russian.

## Getting started

Install NDI Tools/Runtime x64 on PCs sending or receiving NDI; vendor runtime binaries are not redistributed here.

1. Extract the Windows release and run `ScreenCapture.exe`. Settings opens from
   a tray double-click or **Settings** in the tray menu.
2. Under **Screen streaming**, choose a monitor and enable NDI. Output is SDR,
   at the monitor's preferred native resolution, with a 60 FPS delivery clock.
   Lower-resolution fullscreen games stretch to that canvas without letterboxing.
3. For receiving video in OBS/DistroAV, select `PC-NAME (SdrCapture SDR)` with
   BT.709, Limited range, Highest bandwidth and Low latency. Keep OBS in SDR.
4. Under **Instant replay**, choose a save folder, duration, quality and shortcut,
   then enable background recording. Missing FFmpeg tools download automatically
   with a pinned SHA-256 check. They live in your local user-data folder, separate
   from app updates; existing portable `tools` installations also work. A setup
   button appears in this page when tools are missing. NVIDIA NVENC is required
   for GPU recording. The first setup needs internet access.
5. To control another PC, select Host under **KVM & pairing** on the PC with your
   keyboard and mouse. Select Client on the other PC and enter the host name and
   generated pairing code. `Enable-Lan.ps1` can add private wired-LAN firewall
   rules when Windows blocks the connection.

The app uses NDI High Bandwidth for streaming. HEVC, H.264 and AV1 are independent
hardware recording choices; NDI streaming does not use the recording codec.
SDR/HDR desktop capture is converted on the GPU into bounded SDR. SDR shadows
retain their display brightness. HDR highlights use a fixed tone-mapping curve;
Windows SDR-white brightness is compensated without changing Windows settings.

## Virtual camera and audio

**Virtual devices** is available on a receiving PC, and hidden on the KVM host.
Enable reception, select the gaming PC's NDI source and use **Set up devices**.
The camera publishes the source's native resolution rather than forcing 1080p.
Supported frame capacity includes Full HD, 1440p, 4K, 8K (7680×4320), ultrawide
and portrait formats. RGB24/RGB32 and 60/30 FPS formats are advertised.

Receive the source before opening the camera in Discord or OBS. A connected
DirectShow graph keeps its negotiated format; restart the consuming app after
upgrading the camera or changing the source monitor. Discord's account settings,
network and encoder can limit its outgoing resolution and frame rate.
8K format negotiation is tested; 8K60 capture and end-to-end streaming are not
benchmarked on physical 8K equipment.

For network audio, choose a virtual cable output and its paired recording input.
**Install VB-CABLE** downloads the pinned, signed original VB-Audio installer
and requests administrator permission on the receiving PC. Existing cables are
reused. **Set up devices** names the selected input to match the camera; renaming
also requests administrator permission. Original Windows defaults are restored
if the installer changes them. Fully quit and reopen Discord after setup.
Discord may retain an earlier audio selection; choose the matching input once.

Use **Local input or mixer** for a RØDECaster or another recording input, or
**Silent** for no audio. No cable is installed on the gaming host. Driver
installation is explicit; merely pairing computers or updating installs no driver.
Camera binaries live in a versioned user directory so Discord cannot lock the
updater's application files. VB-CABLE is donationware by VB-Audio; its licensing
link remains available in settings.

## Instant replay

- Duration: 5–20 minutes. Recording: 60/120 FPS; native, 720p, 1080p, 1440p or 4K.
- HEVC/H.264/AV1 NVENC when supported by the GPU and installed FFmpeg.
- Game audio, microphone and optional remote-PC audio, mixed or separate tracks.
  Silent is available per source. Track order remains game, microphone, remote PC.
- Clips are grouped by foreground game or app automatically. Local and UNC
  network folders work. Buffering stays local; a separate worker delivers saved
  clips to the destination without blocking capture.
- Clips are exported only by **Save replay**, the configured shortcut or
  `ScreenCapture.exe --save-replay`. Updates, shutdown and settings changes do not
  export a clip. An unsaved buffer is discarded on restart or recording-format
  changes. Already requested deliveries can finish later if a share was offline.

## KVM and monitors

Arrange each physical screen under **Monitor layout**; shared edges must touch.
Two screens on the same remote PC can sit on opposite sides of your host display.
Use × to remove one monitor from edge switching, and drag it back from
**Available monitors**. Its PC remains accessible through KVM. On a headless
server select **Remote view only** under client permissions; it stays outside
physical edge switching. An active desktop/virtual display is still required.

**Computers · KVM** opens a separate chooser. The default launcher shortcut is
Ctrl+Alt+K, edge-lock toggle Ctrl+Alt+Pause, emergency return Ctrl+Alt+Esc.
Monitor shortcuts are configurable. Stream Deck injected keystrokes are supported.
`ScreenCapture.exe --kvm` also opens the chooser. Viewing offers native-resolution
JPEG 95/88 or bandwidth-saving 1080p, with 30/60 FPS target settings. Both peers
need 0.7.2 or later for the dedicated video channel; older peers use snapshot mode.

Physical mouse deltas drive remote movement while games constrain or recenter the
host cursor. Saved desk geometry stays connected through fullscreen resolution
changes. Actual latency and frame rate depend on Windows, GPU load and the network;
zero latency or fresh 60 FPS under every game load is not guaranteed.

File transfer uses Ctrl+C / Ctrl+V in either direction. Experimental desktop-edge
OLE drag portals have been removed because they produced a visible strip.
Temporary transfer cache files expire after four hours; copies pasted into normal
folders stay. Secure desktops and some elevated windows cannot be controlled.

## Settings and updates

**General** contains language, Windows autostart and keep-awake. Keep-awake is on
by default and releases its Windows request when disabled or the app exits;
it changes no power-plan settings. Manual sleep remains available.

**Backup & restore** exports encrypted `.scprofile` files including pairing and
layout. Keep one profile per PC and its password. After reinstalling Windows,
restore it on the original PC; reselect audio devices if Windows changes their IDs.

**Updates** verifies RSA-signed manifests, SHA-256 package/file hashes and update
startup acknowledgement. **Update all PCs** stages compatible paired clients,
restarts them, and waits for their return before updating the host. Settings,
recording tools and saved clips are preserved. Reopen Discord/OBS to load an
updated camera DLL. Releases are published at:
https://github.com/Aleksei-Melnik/ScreenCapture/releases

Existing installs retain `%LOCALAPPDATA%\SdrCapture`, pairing and the NDI source
name. `SdrCapture.exe` is a compatibility launcher for old shortcuts and firewall
rules. Exit the app before a manual in-place extraction; retain the `tools` folder.

## Building and validation

Build with .NET 10 and Visual Studio C++ Windows SDK: `./Build.ps1`.
Managed deterministic tests cover update integrity/rollback, audio timing, KVM,
UI responsiveness and language switching. Camera probes exercise real DirectShow
negotiation, RGB24/32, 30/60 FPS, native dimensions and source pauses. GPU tests
exercise HDR-to-SDR bounds, SDR gray ramps, cursors and stretched output. Local
fixtures do not substitute for game-load, remote-network or hardware benchmarks.

See `LICENSE` and `THIRD-PARTY-NOTICES.md` for dependency terms. NDI and VB-CABLE
remain their vendors' products. FFmpeg binaries are not redistributed in releases.
