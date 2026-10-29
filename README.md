# DeskGlide

KVM, screen and audio sharing, and instant replay for Windows 11. Connect your
PCs, arrange their monitors and move between them with one keyboard and mouse.
The interface supports English and Russian.

## Screenshots

Press **PrintScreen**, select an area and annotate it with pen, lines, arrows,
rectangles, marker or text. Compact drawing tools follow the selection; copy
and save buttons sit beside its border. Undo/redo and opaque redaction are
included. PrintScreen opens the editor; editing uses the onscreen buttons.
HDR desktops are tone-mapped to ordinary sRGB PNGs
using each monitor's Windows SDR brightness; HDR remains enabled. Screenshots
stay local. **Settings → Screenshots** chooses the folder and PrintScreen
preference. Another app using that key must release it; capture is also available
in the DeskGlide tray menu. [Controls and validation](docs/SCREENSHOTS.md).

## Stream Deck

Install the [DeskGlide plugin from Elgato Marketplace](https://marketplace.elgato.com/product/deskglide-756e68ee-b961-40bb-b19c-cb0a39635679).
**Settings → Stream Deck → Install plugin** opens this Marketplace listing.
The Stream Deck page also shows the plugin's connection.
Save replay, switch to a monitor, open KVM or a particular PC/server, lock mouse transitions, and toggle
replay/streaming with live state on the keys. On a receiving PC, replay and toggle
actions can target the paired controller PC. Requires DeskGlide 0.7.20+ and Stream
Deck 7.1+. [Setup guide](docs/STREAM-DECK.md) · [Privacy](docs/STREAM-DECK-PRIVACY.md).
A plugin package is also available from
[GitHub Releases](https://github.com/Aleksei-Melnik/DeskGlide/releases/latest) for manual installation.

## Getting started

NDI Runtime is set up automatically when an NDI sender, receiver or source search
first needs it. The app downloads the original NDI 6 Runtime installer, checks
its pinned SHA-256 and Windows signature, and opens the vendor's license wizard.
Complete that one-time wizard and Windows' administrator prompt. Existing NDI
Tools/Runtime installations are reused. A cancelled setup can be retried under
**Screen streaming → Set up NDI Runtime**; it will not keep reopening by itself.
KVM and local replay do not require NDI Runtime.

1. Download the single `DeskGlide.exe` from Releases and run it. No app installer
   or separate .NET installation is needed. Settings opens from
   one left click on the tray icon or **Settings** in its right-click menu.
2. Under **Screen streaming**, choose a monitor and enable NDI. Output is SDR,
   at the monitor's preferred native resolution, with a 60 FPS delivery clock.
   Lower-resolution fullscreen games stretch to that canvas without letterboxing.
3. For receiving video in OBS/DistroAV, select `PC-NAME (SdrCapture SDR)` with
   BT.709, Limited range, Highest bandwidth and Low latency. Keep OBS in SDR.
4. Under **Instant replay**, choose a save folder, duration, quality and shortcut,
   then enable background recording. Missing FFmpeg tools download automatically
   with a pinned SHA-256 check. They live in your local user-data folder, separate
   from app updates. Existing `tools` folders are migrated into user data in the background. A setup
   button appears in this page when tools are missing. GPU recording selects
   NVIDIA NVENC, AMD AMF or Intel Quick Sync after a short encoder preflight.
   The first dependency setup needs internet access.
5. To control another PC, select Host under **KVM & pairing** on the PC with your
   keyboard and mouse. Select Client on the other PC and enter the host name and
   generated pairing code. `Enable-Lan.ps1` can add private wired-LAN firewall
   rules when Windows blocks the connection.

The app uses NDI High Bandwidth for streaming. HEVC, H.264 and AV1 are independent
hardware recording choices; NDI streaming does not use the recording codec.
SDR/HDR desktop capture is converted on the GPU into bounded SDR. SDR shadows
retain their display brightness. HDR highlights use a fixed tone-mapping curve;
Windows SDR-white brightness is compensated without changing Windows settings.

The executable contains the Windows Desktop runtime, KVM codec, virtual camera
and dependency notices. Native runtime files use Windows' internal bundle cache;
camera components, settings, keys and recording tools use the per-user data folder.
No libraries or `tools` folder need to be kept next to `DeskGlide.exe`. FFmpeg is
stored under `%LOCALAPPDATA%\SdrCapture\RecordingTools`, never on the desktop.
NDI's setup download also stays in the user-data folder. Its vendor installer
installs the Runtime into Program Files. VB-CABLE remains optional; the audio
driver still needs explicit setup and Windows approval.
Signed updates replace the current EXE. Older folder installations migrate in
place, preserving the active launcher name, settings, pairing and recording tools.
Only files owned by the signed app package are removed; unknown user files remain.

## KVM and administrator apps

On the Host, **KVM & pairing → Administrator apps** is enabled by default from
0.7.27. Accept Windows' administrator prompt at startup to switch away from apps
such as MSI Afterburner, RTSS and Rufus while they have focus. A separate local
input helper receives those rights. The main app, network connections, screen
capture, clipboard, recording and updater keep their ordinary rights.

Clients keep ordinary input control by default, so **Updates → Update all PCs**
can update them without visiting each screen. If you need to control elevated
windows on a Client, enable the same option there and approve Windows locally.
Protected Windows security prompts, lock screens and other desktops stay local.
The app does not disable UAC or change another program's privileges.

If approval is canceled, standard control continues. An elevated foreground app
then prevents edge switching until you focus an ordinary window or the desktop.
Loss of input access or a disconnected input helper releases the remote session
and returns control here. **Ctrl+Alt+Esc** also returns local control on the
interactive desktop. The **Status** page shows which input mode is active.

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

Start with **High** quality. For 2560 × 1440 at 120 FPS, use AV1 when your GPU
supports hardware AV1 recording; HEVC is the alternative. For 1920 × 1080 at
60 FPS, High with AV1 or HEVC is also a good starting point. Choose H.264 for
broader compatibility with players and editing software. Unsupported hardware
choices report an error rather than switching to CPU encoding.

AV1 playback also needs a current decoder. Old MPC-HC/K-Lite installations with
LAV 0.74 cannot hardware-decode AV1 and may play 1440p120 recordings in slow
motion. Update [MPC-HC](https://github.com/clsid2/mpc-hc/releases/latest) or your
codec pack; on Windows 10/11, use MPC Video Renderer with **D3D11** hardware
decoding in LAV Video Decoder. AV1 hardware support was added in
[LAV 0.75](https://github.com/Nevcairiel/LAVFilters/releases/tag/0.75).
HEVC or H.264 is an alternative when the playback GPU cannot decode AV1.
Changing the recording preset does not repair an outdated playback decoder.

| Quality | When to use it | NVIDIA quality target | Maximum bitrate at 1440p60 |
| --- | --- | --- | --- |
| Ultra | More detail in difficult motion; larger clips | CQ 18 | 60 Mbps |
| High | Recommended starting point | CQ 21 | 40 Mbps |
| Medium | Smaller clips with more visible compression | CQ 25 | 24 Mbps |
| Low | Save disk space when image detail is less important | CQ 29 | 14 Mbps |

NVIDIA uses variable bitrate: these are ceilings, not a fixed bitrate. The
ceiling scales with pixel count and frame rate, within 6–200 Mbps. At 1440p120,
the ceilings are 120/80/48/28 Mbps; at 1080p60 they are 34/23/14/8 Mbps. Actual
file size depends on the scene and codec. AMD uses constant quantizer settings
and Intel uses its quality control, so their bitrates can differ. Quality does
not select resolution or FPS, and Ultra does not fix recording stutter. If
your hardware cannot sustain a format, lower FPS or resolution. High at 60 FPS
is the safer starting point on older GPUs. Recording has some capture and GPU
overhead; zero game-FPS impact is not guaranteed.

The quality menu in the app explains each level and shows the NVIDIA bitrate
ceiling for the selected resolution and FPS. The numbers update when these
settings change. Both English and Russian labels are available.

- Duration: one slider from 1 minute to 2 hours, in one-minute steps.
  The selected time is shown beside it. Old sub-minute profiles use a one-minute
  buffer; existing minute-based durations are preserved.
  Recording: 60/120 FPS; native, 720p, 1080p, 1440p or 4K.
- HEVC/H.264/AV1 through NVIDIA NVENC, AMD AMF or Intel Quick Sync, when supported
  by the GPU and driver. Selection checks the actual requested resolution, frame
  rate and codec. Unsupported choices produce a clear error; the app never
  silently falls back to heavy CPU encoding or changes the selected codec.
  Physical RTX 5070 and Radeon integrated-GPU tests cover 1440p moving-frame
  replay, muxing and decoding: NVIDIA H.264/HEVC/AV1 and AMD H.264/HEVC at
  60/120 FPS in the tested configurations. The integrated Radeon does not encode
  AV1. Intel selection is tested deterministically; physical Intel recording and
  sustained game-load performance on all GPU models remain unverified.
  See [hardware validation](HARDWARE-VALIDATION.md) for measurements and limits.
- Game audio, microphone and optional remote-PC audio, mixed or separate tracks.
  Silent is available per source. Track order remains game, microphone, remote PC.
- Clips are grouped by foreground game or app automatically. Local and UNC
  network folders work. Buffering stays local; a separate worker delivers saved
  clips to the destination without blocking capture.
- A gentle, two-note save chime plays only after a clip reaches the chosen
  destination. Enable or disable it, adjust its volume, or preview it under
  **Instant replay → Save notification**. Default volume is 25%.
- Buffer storage is local and follows the chosen duration. A long, high-quality
  buffer can use tens of gigabytes; it requires enough free space. Increasing
  duration fills the extra history over time. Saves are trimmed to complete
  frames without re-encoding. After a successful Save, the next clip contains
  only new footage since that Save, up to the chosen duration. A failed export
  does not discard unsaved footage. Recording continues without restarting its
  encoder when you save.
- Audio-enabled replay uses one hardware video encoder and a lightweight
  FFmpeg process that copies compressed video and combines it with audio.
  Seeing two recording `ffmpeg.exe` processes is expected. Video is encoded
  before waiting for local/remote audio, reducing retained raw frames without
  changing the selected quality. The matched 1440p120 AV1 memory measurement
  and full-app limits are documented in [hardware validation](HARDWARE-VALIDATION.md).
- Clips are exported only by **Save replay**, the configured shortcut or
  `DeskGlide.exe --save-replay`. Updates, shutdown and settings changes do not
  export a clip. An unsaved buffer is discarded on restart or recording-format
  changes. Already requested deliveries can finish later if a share was offline.

## KVM and monitors

Arrange each physical screen under **Monitor layout**; shared edges must touch.
Two screens on the same remote PC can sit on opposite sides of your host display.
Use × to remove one monitor from edge switching, and drag it back from
**Available monitors**. Its PC remains accessible through KVM. On a headless
server select **Remote view only** under client permissions; it stays outside
physical edge switching. An active desktop/virtual display is still required.

Monitor layout removes disconnected displays when their PC reports its current
screens, including obsolete `DISPLAY` names left by a monitor driver reinstall.
Offline PCs keep their saved layout. When a PC has one saved and one current
screen, a changed display name retains its position, shortcut and edge exclusion.

**Computers · KVM** opens a separate chooser. The default launcher shortcut is
Ctrl+Alt+K, edge-lock toggle Ctrl+Alt+Pause, emergency return Ctrl+Alt+Esc.
Monitor shortcuts are configurable. Stream Deck injected keystrokes are supported.
`DeskGlide.exe --kvm` also opens the chooser. Viewing offers native-resolution
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
https://github.com/Aleksei-Melnik/DeskGlide/releases

Existing installs retain `%LOCALAPPDATA%\SdrCapture`, pairing and the NDI source
name. Folder updates retain the name of the running launcher (`ScreenCapture.exe`,
`SdrCapture.exe` or `DeskGlide.exe`) to preserve its shortcut and firewall rules.
Fresh downloads use a single `DeskGlide.exe`; the singleton prevents duplicate senders.
Old `.scprofile` backups remain readable. Custom camera/audio names are kept.
The signed updater archive retains the `ScreenCapture-*` name so existing
clients can migrate; the compatibility `DeskGlide-*` ZIP contains identical files.
After migration, updates download and replace only the signed portable EXE.
The renamed GitHub repository redirects old update URLs. Do not recreate a
repository under the old name. Exit the app before manually replacing the EXE.
Legacy recording tools migrate automatically; unrelated files in an old `tools`
folder are preserved. A busy encoder is cleaned up on a later launch.

## Building and validation

Build with .NET 10 and Visual Studio C++ Windows SDK: `./Build.ps1`.
Build the single executable with `./scripts/Build-Portable.ps1 -Payload ./app`.
Managed deterministic tests cover update integrity/rollback, audio timing, KVM,
UI responsiveness and language switching. Camera probes exercise real DirectShow
negotiation, RGB24/32, 30/60 FPS, native dimensions and source pauses. GPU tests
exercise HDR-to-SDR bounds, SDR gray ramps, cursors and stretched output. Local
fixtures do not substitute for game-load, remote-network or hardware benchmarks.

See `LICENSE` and `THIRD-PARTY-NOTICES.md` for dependency terms. NDI and VB-CABLE
remain their vendors' products. FFmpeg binaries are not redistributed in releases.
