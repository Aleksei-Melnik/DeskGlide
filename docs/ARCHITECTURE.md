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

## Capture, replay and audio

`StreamEngine` owns separate capture and NDI send workers. `DesktopCapture`
uses DXGI duplication and `ColorPipeline` GPU conversion to bounded SDR;
preferred native canvas dimensions are independent of fullscreen resolution.
Reference-counted `VideoBuffer`/recording pools keep ownership explicit.
Capture can continue for replay while NDI is disabled.

`ReplayRecorder` packs offered frames into a bounded history and owns rolling
compressed segments. `RecordingEncoder` selects/preflights GPU encoding;
`RecordingVideoEncoder` and pipe writers separate video encoding from audio
muxing so audio arrival does not retain uncompressed video for the whole wait.
Saving pins completed segments and `ReplayDelivery` copies finalized clips.
`TimedAudioCapture` maps WASAPI sample/device timestamps to a 48 kHz timeline;
`ReplayAudio` reads local/KVM sources without changing the NDI latency clock.

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
