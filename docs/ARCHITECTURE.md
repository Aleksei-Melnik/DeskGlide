# DeskGlide maintenance map

Working checkout: `src/ScreenCapture.csproj`, .NET 10 Windows x64, WPF settings
with WinForms interop. Keep assembly identity `ScreenCapture` and all legacy
launcher/update payload names: deployed updaters require them.

## Startup and ownership

`Program` handles narrow CLI/test/helper commands before taking the application
singleton. Ordinary startup creates `TrayApp`, the WPF dispatcher and the
WinForms interop pump. `TrayApp` owns capture/replay, KVM, clipboard, Stream Deck,
the update coordinator and lifecycle disposal. Settings changes restart only
the relevant service. `Ui/SettingsWindow` is the current interface; older
WinForms settings/viewer code remains for compatibility and tests.

`PortableResources` reads embedded camera/plugin/license resources and checks
cached content. Settings, pairing, recordings/tools and camera registration live
in per-user data; they must survive updates. `MediaShutdown` bounds native media
shutdown without freeing resources that a blocked worker still owns.

## KVM transport and identity

`KvmService` owns Host/Client reconnect loops and independent control, bulk,
audio and video channels. `KvmWire` provides TLS, certificate pinning and a
pairing secret challenge. Peer metadata includes screen inventory, installed
version and feature protocol versions. A bounded nonblocking control queue
coalesces only adjacent pointer moves; key/button/release boundaries retain
their order. Clipboard manifests reject traversal, device names and oversized
payloads. Files/images use bulk transfer, integrity checks and app-owned caches.

`KvmLayout` merges saved logical desk geometry with current inventories.
Physical resolution can change without moving logical monitors. Reconciliation
removes obsolete screens only for online peers, preserves offline layouts and
handles a single renamed monitor after a driver update. Do not deduplicate
physical screens merely because their resolution matches.

## Input ownership and privilege boundary (0.7.27)

`KvmInputManager` is the TrayApp facade. In standard mode it owns a
`KvmController`. In administrator mode it launches the same EXE with `runas`
and a private random named pipe; no pairing secret or token is passed on the
command line. Host default is enabled, Client/Off default is disabled unless
explicitly configured. Startup consent runs asynchronously so the ordinary
app can acknowledge an update and reconnect before consent completes.

`KvmInputHelper` runs only input capture/injection. It opens no network listener,
UI, capture, clipboard, updater or recording subsystem. The parent verifies
the pipe client's PID against the launched child. The child requires elevation,
checks parent PID/start time, session, EXE path and actual pipe server PID.
Pipes have an explicit ACL/owner for the actual user SID, deny network logons,
and the child connects with anonymous impersonation to prevent borrowing its
elevated token. Do not substitute .NET 10 `CurrentUserOnly`: its token-owner
comparison fails across elevation (dotnet/runtime#123903). Frames are bounded to 128 KiB, outgoing queues to
512 items. Only sanitized layout/shortcut/peer data and input commands cross
this boundary. Injection commands are allowed only in Client role. Invalid
commands, a full queue, parent loss or missing heartbeat terminate the helper
and release capture. It never switches input desktops or suppresses UAC.

`KvmController` owns a dedicated STA message pump, low-level mouse/keyboard
hooks, Raw Input and a 100 ms access watchdog. Local-to-remote activation
requires accessible foreground/input desktop, a successful physical cursor
warp and a connected peer. Logical remote ownership is committed only after
the warp. Physical raw deltas advance the remote cursor; synthetic game
recentering is ignored. The low-level hook suppresses local actions while
remote. Loss of access returns local and sends release before more forwarding.
Missing mouse hooks detected against physical raw movement and recovery from
an inaccessible desktop reinstall hooks. Monitor refresh must not reset an
unchanged active session.

`KvmInput` validates mouse/key/release messages and checks `SendInput` success.
Its pressed set contains only accepted input. Failed UP events remain pending
and are retried before subsequent input. Release does not move the cursor and
does not invent releases for buttons never pressed by KVM. An inaccessible
desktop defers release; it never injects into a Windows security prompt.
Receivers report `input-blocked`; Hosts return local. Legacy peers safely ignore
the added control message.

These paths intentionally do not contain a foreground fullscreen heuristic.
An ordinary process cannot control/suppress higher-integrity apps through
normal Windows input APIs. A desktop click changes the foreground integrity
and explains why the pre-0.7.27 issue appeared to depend on window focus.

## Viewer keyboard ownership (0.7.32)

The WPF viewer passes its input manager to the embedded WinForms viewport.
It registers the peer, root HWND and viewport HWND through the ordinary or
elevated controller. `KvmKeyboardFocus` checks that both handles belong to the
ordinary parent process and share a root; at every key, foreground/root and
actual GUI-thread focus must still match the viewport. The helper adds only a
Host-side `viewer-keyboard` command on its existing private, process-bound pipe.
It cannot register another application's window. No new network command or
keyboard logging is introduced.

Viewer system keys now use the same low-level suppression/transport path as
seamless control, preserving scan codes and extended flags. Form key events
must not forward another copy. Viewport metadata can remain registered while
toolbar focus is local, so returning focus does not require the first modifier
to wait for an IPC/UI round trip. Focus loss, disabled control, disconnect and
emergency return release remote presses. Per-key route generations consume
remaining repeats/UPs after a released session, preventing a stray local Win/Alt
release or transfer of a held key to a different peer. Keys whose DOWN was local
retain their local lifetime. Ctrl+Alt+Esc and the configured KVM commands remain
local; viewer F11 retains its fullscreen behavior. An inactive viewer closing
must not disable keyboard capture in another active viewer.

## Capture, replay and audio

`ScreenshotHotkey` registers PrintScreen after KVM low-level routing. `TrayScreenshots`
owns a cancellable, one-at-a-time background capture and its editor lifetime.
`ScreenshotFrame` acquires each local DXGI output, keeps FP16 HDR through
tone mapping, compensates per-monitor Windows SDR white and rotates into a
physical-pixel virtual canvas. It never consumes recording UYVY or changes HDR.
`ScreenshotEditor` freezes before display, pauses KVM edge switching and offers
original local annotation/crop tools. `ScreenshotFiles` writes sRGB-tagged PNG
through an atomic sibling staging file; copy offers bitmap and tagged PNG.
No upload, new network command or elevation is involved. See `SCREENSHOTS.md`.

`StreamEngine` owns separate capture and NDI send workers. `DesktopCapture`
uses DXGI duplication and `ColorPipeline` GPU conversion to bounded SDR;
preferred native canvas dimensions are independent of fullscreen resolution.
Reference-counted `VideoBuffer`/recording pools keep ownership explicit.
Capture can continue for replay while NDI is disabled.

Since 0.7.28, desktop conversion queues up to three UYVY staging buffers and
maps only a completed copy (`DO_NOT_WAIT`). It keeps the original DXGI timestamp,
drains on desktop timeouts and resets the queue on format/size changes. This
avoids waiting immediately on GPU work and retains a bounded delivery queue.
Since 0.7.31, replay acquires at 120/240 Hz for 60/120 FPS, briefly waiting for a
new desktop presentation (at most 8/4 ms) so updates just after a poll are not
lost. It releases ownership after queuing the GPU copy and drains all completed
copies each iteration. Deadlines derive from their index without accumulated
fractional-tick drift; encoding stays at 60/120 FPS and the
independent NDI sender stays at 60 FPS. Do not claim a zero-copy desktop-to-NVENC path: replay still
packs CPU UYVY into NV12 and transports native-sized raw frames to FFmpeg.

`ReplayRecorder` retains offered immutable frame references in a bounded
conversion queue (eight images, 64 MiB, allowing one image when larger).
A separate worker packs NV12 with AVX2/SSSE3/scalar paths, retaining source
timestamps and recording generation. A completed conversion for an obsolete
generation is rejected; disable/shutdown drain pending references. It keeps a
bounded NV12 history and owns rolling
compressed segments. `RecordingEncoder` selects/preflights GPU encoding;
`RecordingVideoEncoder` and pipe writers separate video encoding from audio
muxing so audio arrival does not retain uncompressed video for the whole wait.
Saving pins completed segments and `ReplayDelivery` copies finalized clips.
`TimedAudioCapture` maps WASAPI sample/device timestamps to a 48 kHz timeline;
`ReplayAudio` reads local/KVM sources without changing the NDI latency clock.

The recorder waits briefly for initial local audio packets, then anchors its
video/audio epoch to a real source image at or after audio readiness. Startup
waits are bounded for unavailable/silent inputs and static desktops. Exact NV12
comparison discards consecutive unchanged images even when DWM assigns them
new presentation timestamps, retaining the first image's timestamp. Recording
matches distinct images within 20 ms of the output deadline. Near-output-rate
sources conserve chronological images; sustained oversampling can select the
nearest image only when the continuation supplies the fully buffered deadlines
(up to eight). A short source burst or insufficient lookahead keeps chronological
selection. Missing images fall back to the latest preceding image.
The timing allowance uses the existing 100 ms recording playout delay. This
preserves source order through ordinary jitter; it cannot invent frames missing
from the source. `RecordingSdr` tags raw input frames as limited
BT.709 before encoding, in addition to output tags. Container metadata alone is
insufficient for AV1 NVENC sequence-header primaries/transfer.

`RecordingScale` scales native-sized NV12 in the video encoder instead of
resizing every recording deadline on the C# writer thread. NVENC uses CUDA
upload/scale/padding and keeps the resized surface on the GPU. Preflight includes
the actual source and output dimensions; imported tools without CUDA filters
can fall back to FFmpeg's software scaler before trying another GPU vendor.
Other vendors use the optimized software scaler. A rare source-size transition
within an existing session is normalized to that session's raw input canvas;
ordinary capture already retains the selected monitor's preferred native canvas.
Silent recordings also use the separate video-encoder transport when scaling.

`ReplayAppContext` samples the foreground application into a bounded activity
timeline. Saving resolves the dominant application over the exported interval;
DeskGlide/Stream Deck save controls are transparent. Ordinary apps are not
blacklisted as Desktop. Local activity metadata is not embedded into media or
sent to a server. `GroupByApp=false` bypasses the application subfolder.

`KvmVideo`/GPU capture serve remote viewers separately from NDI.
`DiscordReceiver` and native DirectShow camera components receive NDI into a
versioned per-user camera location. Optional vendor runtime/recording tools
use pinned integrity verification; device/driver installation remains explicit.

`StreamDeckBridge` exposes a private local pipe with bounded commands;
`DeckRemote` forwards authorized actions to the paired Host. `TrayStreamDeck`
uses the same input manager/layout as mouse switching.

## Signed updates and release discipline

`Updates` validates the embedded RSA public key, signed manifest and payload
hashes before staging. `UpdateInstaller` handles replacement, acknowledgement,
rollback and legacy folder-to-portable migration. `UpdateCoordinator.InstallAll`
prepares all compatible peers, updates Clients, waits for their reported new
versions, then updates the Host. Do not bypass signature checks or change this
order to work around an input issue. Dispose the input manager/helper before
replacing the application EXE.

From 0.7.29, staging and backups remain in AppData and directory-access checks
open a directory handle without creating probe files. A protected destination
uses a separate Windows-approved `UpdateFileSession` worker. The ordinary
coordinator retains responsibility for restart and health checks, so elevation
does not spread from the worker to the app. The worker independently verifies
the signed plan/payload and binds its destination to the live original EXE;
its private, bounded pipe checks the Windows user and both process identities.
Approval is obtained before the app hands over. Only install/commit/rollback
are accepted; a lost connection rolls back an unfinished file transaction.
Rollback retains the journal in memory and hashes the original backup bytes;
neither an editable disk journal nor altered backup content is trusted.
Versions through 0.7.28 can still stop in their old permission probe before
launching this installer and need a one-time approved update or per-user copy.
From 0.7.30, portable-to-portable transactions own only the running EXE;
legacy component cleanup is reserved for folder-to-portable migrations.

0.7.26 established the current signing key after the earlier key was lost.
**Keep that key for every future release.** The private key is outside the repo
and backed up in GitHub Actions' encrypted `DESKGLIDE_RELEASE_PRIVATE_KEY`
secret. It must never be printed, committed or included in an artifact.
See [RELEASING.md](RELEASING.md) for publishing commands and checks.

## Verification and remaining hardware coverage

`--kvm-input-tests` runs controller/injection regressions with fake native input,
and a real process-bound pipe echo fixture. It never installs hooks or changes
OS input, clipboard, settings or live connections. `--kvm-test` checks TLS,
queue ordering, file/image transfers and dedicated-thread hook startup.
`--monitor-layout-tests` covers inventories, driver renumbering and geometry.
`--update-tests` covers signatures, staging, rollback and old payloads.
`--kvm-helper-approval-test` explicitly exercises a real approved helper with
native hooks, private cross-integrity IPC and clean shutdown, without activating
remote control or injecting input. It requires an interactive desktop and is
excluded from unattended CI. This integration check passed on the gaming PC.

`.github/workflows/build.yml` runs media, lifecycle, UI, KVM, update and plugin
checks against both folder and standalone builds. UI/native clipboard tests
require an isolated desktop without an installed DeskGlide instance running.
Never interrupt a user's live recording or inject clicks to run a regression.

Before claiming complete hardware validation, exercise actual Host/Client edge
transitions with Afterburner/RTSS focused, left/right clicks, keyboard modifiers,
emergency return, UAC cancel/approve, lock/unlock, peer disconnect and Update all
PCs. Deterministic fixtures and Windows CI do not establish that physical result.
