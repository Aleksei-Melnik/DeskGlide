DeskGlide 0.7.31

- Fixed a remaining 120 FPS repeat/drop pair at the recording deadline. The selector could discard an available image just outside its 3/4-frame acceptance window, promote the next image early and repeat it on the following deadline. Chronological selection now allows one output period on either side, within the existing 100 ms playout buffer. Real source stalls retain their actual timing; no motion interpolation is used.
- Replay acquisition uses an integer multiple of the recording rate (120/240 Hz for 60/120 FPS), replacing the 72/144 Hz ratio that introduced recurring short/long motion steps. Pacing deadlines are derived from their frame index so fractional QPC ticks do not accumulate clock drift. NDI and recording retain their configured output rates.
- Acquisition briefly waits for a new desktop presentation instead of missing updates that arrive just after a zero-timeout poll. GPU readback remains asynchronous. Recording aligns its video/audio epoch to a real source image and selects the closest subsequent image to each recording deadline, removing phase-dependent motion jitter.
- NV12 packing runs on a separate, bounded worker instead of holding up desktop acquisition. Source references retain their presentation timestamps and recording generation; disabling recording drains pending frames. The queue is limited to eight images and 64 MiB (or one image when larger), with oldest-frame drop accounting during overload.
- AVX2 processors pack 16 pixels at a time while preserving the same limited-range SDR BT.709 luma/chroma codes. SSSE3 and scalar fallbacks remain supported. Recording uses the existing NDI HDR-to-SDR conversion. Audio clocks, app-based save folders, update signing and protected-folder update handling are retained.

Validation: regression tests reproduce the narrow-window repeat with an actual timing trace; queue tests cover blocked conversion, retained pixels/timestamps, bounded overload, clearing and shutdown. Bit-exact pixel tests run with AVX2, SSSE3 and scalar paths. Fullscreen desktop acquisition, simultaneous NDI, encoder/scaling coverage and long audio/save-boundary checks are documented in docs/RECORDING-VALIDATION-0.7.31.md.

Previous release: 0.7.30

DeskGlide 0.7.30

- Updates work from protected folders such as `C:\` and Program Files: packages, jobs and backups stay in AppData; the access check creates no `.update-write-test` file beside the application. Windows requests administrator approval only for a separate file-replacement worker when necessary. The running application retains its existing privilege level, and canceling approval leaves it open.
- Portable updates replace only their running EXE. Neighboring files are preserved even when their names match components of an old folder package. Legacy folder-to-portable migrations retain their cleanup and rollback. Signatures, payload hashes, process-bound private IPC and hash-checked rollback backups remain required. The existing update signing key, settings and pairing are retained.

**Older protected copies (0.7.26–0.7.28):** their old access check can stop before the new installer starts. They need one final update started as administrator, or a copy launched from a writable per-user folder. Subsequent updates request approval themselves when needed; no permanent administrator mode or Windows permission changes are required. Protected remote PCs require local Windows approval. Installations in writable AppData folders update without this prompt.

Validation: restricted temporary-folder access, no probe-file creation, ordinary writable-folder updates, approval cancellation reporting, private bounded IPC, transaction commit/rollback/interruption, tampered-backup rejection and old update jobs. An additional portable transaction runs beside matching legacy filenames and verifies that only its EXE changes. Existing signed-package and portable-migration tests are retained. Interactive UAC approval is not part of unattended CI.

Previous release: 0.7.29

- Fixed updates from protected application folders such as `C:\` and Program Files. Packages, signatures, jobs and backups stay in the user's AppData. Checking directory access no longer creates an `.update-write-test` file beside the application. Writable folders on C: continue to update without administrator approval.
- When file replacement requires administrator rights, Windows asks for approval for a separate, local file worker. The coordinator downloads/verifies the package and restarts DeskGlide at its existing privilege level. Approval happens before the app hands over; canceling leaves the running app and files intact. Existing RSA signatures, payload hashes, parent-process checks and rollback remain required. Private IPC is restricted to the same Windows account and expected processes. Rollback uses an in-memory journal and rejects altered backup files.

**Updating an older protected copy:** 0.7.26–0.7.28 contain the earlier permission check, which can stop before the new installer starts. Those copies need one final update started as administrator, or a replacement launched from a writable per-user folder. From 0.7.29, the update button requests the necessary Windows approval itself. No permanent administrator mode or Windows permissions changes are required. Protected folders on remote PCs still require local Windows approval; the controller cannot approve UAC remotely.

Validation: restricted temporary-folder permissions reproduce the access-denied case; writable-folder checks leave no probe files. Regression tests cover transaction commit, rollback and interrupted handover, private bounded IPC and peer process identity, canceled approval reporting, tampered backups and compatibility with old update jobs. Existing signed package, portable migration, user-file preservation and version checks are retained. Interactive UAC approval is not automated during gaming. Recording changes and further 120-FPS tests are deferred.

Previous release: 0.7.28

- Replay recording now preserves chronological source frames across small capture-clock variations. This avoids artificial repeat/drop pairs when a source frame lands just after a recording deadline. Capture pacing also retains its clock phase after a missed slot.
- Recording at a resolution different from the monitor no longer resizes every pixel in a C# loop on the recording deadline. NVIDIA uses preflighted CUDA scaling directly into NVENC; other paths use FFmpeg's optimized scaler. Aspect ratio, black letterboxing and SDR metadata are preserved. The lag watchdog now uses elapsed time so a severely overloaded encoder cannot remain indefinitely in its startup grace period.
- Desktop capture reads completed GPU copies from a bounded staging queue instead of blocking immediately on the copy just submitted. Replay polls with 20% timing headroom and drains completed copies each poll; recording and NDI retain their requested output rates. SDR conversion is unchanged. Queued readback can add one capture interval of delivery latency; this is bounded to three staging surfaces and retains the original presentation timestamps.
- Recordings explicitly carry limited-range SDR BT.709 on both raw input frames and encoded output. Previously AV1 sequence headers could report unspecified primaries/transfer even though the MP4 container said BT.709. Recordings use the same HDR-to-SDR conversion and color values as NDI, without writing an HDR stream.
- Application folders describe the app used for most of the saved interval. Briefly switching to the desktop or opening Save no longer relabels a CS2 clip as Desktop. Browsers, OBS, Discord and other ordinary apps are recognized; File Explorer is distinguished from the desktop. Turning off app grouping now actually saves directly to the selected folder.

The update signing key and existing settings/pairing are retained. Install through **Updates → Update all PCs**. If an older copy is stored directly in a protected location such as `C:\`, its existing updater may require that copy to be started as administrator for this update.

All 24 NVIDIA recording combinations passed: AV1/HEVC/H.264, 720p/1080p/1440p/4K, 60/120 FPS, three consecutive saves, decoded source-frame conservation, elementary-stream SDR headers and synthetic AAC continuity. Twelve additional native/scaled hardware color checks matched flat NDI-derived SDR patches. Long runs passed ten minutes of AV1 1440p120 and five minutes each of HEVC 4K120 and H.264 1080p120, with uninterrupted consecutive saves. The ten-minute live Windows-audio test had no missing samples and measured up to 6 ms A/V offset without accumulating drift. Actual simultaneous NDI receive/recording checks passed at H.264 1080p60, AV1 1440p120 and HEVC 4K120. Deterministic capture-clock, folder attribution, audio timeline and signed-update regressions are retained. A limited sequential H.264 1440p60 game comparison found no near-identical adjacent images in either NVIDIA App or DeskGlide; 120-FPS desktop-source repetitions remain measurable. These checks do not establish identical performance or image quality in every game. See [recording validation](https://github.com/Aleksei-Melnik/DeskGlide/blob/main/docs/RECORDING-VALIDATION-0.7.28.md) for measurements and coverage limits.

Previous release: 0.7.27

- Fixed KVM input ownership when an administrator app has focus. The Host now offers a Windows-approved local input helper so edge switching and input suppression can work with MSI Afterburner, RTSS, Rufus and similar elevated apps. Networking, clipboard, capture and recording remain in the ordinary process. No driver is installed and Windows security prompts/lock screens stay local.
- Input failures now return control to this PC and tell the Host when the receiving PC cannot accept input. Keys and mouse buttons are tracked only after Windows accepts a press; failed releases are retained and retried. Cursor activation checks both the Windows result and the actual cursor position. Mouse hook loss and recovery from an inaccessible desktop re-establish input hooks.
- On Hosts, administrator app control is enabled by default and Windows asks for approval when DeskGlide starts. Clients retain standard input control by default; it can be enabled locally if elevated windows must be controlled there. Canceling approval falls back to standard control. Input mode is visible on the Status page.

Update from **0.7.26 → Updates → Update all PCs**. The existing update signing key is retained. Clients update first and reconnect; the Host updates last. Accept the Windows prompt on the Host after restart. No manual EXE replacement is needed from 0.7.26.

Validation: regression tests cover focus/access loss, local return before forwarding a click, failed cursor activation, modifier recovery, accepted/failed key and mouse injection, deferred releases, no phantom right-button release, private IPC process identities, ordered delivery, malformed frames, bounded queues, child exit on pipe closure and exclusion of pairing credentials from helper configuration. A real Windows-approved elevated helper was also tested for hook startup, cross-integrity private IPC and clean exit. These checks do not inject OS input. A physical two-PC Afterburner/RTSS test still needs verification after updating.

Previous release: 0.7.26

**One-time installation:** download `DeskGlide.exe`, save any needed replay, quit the old app from the tray and replace its EXE. This release establishes a new update signing key. Versions 0.7.25 and earlier cannot install it through their old automatic updater. From 0.7.26 onward, signed automatic updates continue normally. Settings and pairing are preserved.

- Fixed phantom monitors in Monitor layout after a monitor driver reinstall changes a Windows display name, for example DISPLAY5 to DISPLAY1. Connected PCs now use their current screen inventory; obsolete screens are also removed from Available monitors when the layout is saved.
- When one saved screen is renamed to one current screen, its desk position, logical dimensions, shortcut and edge exclusion are preserved. PCs that are offline retain their saved layout, and real monitors with identical resolutions remain separate.

Validation: a regression fixture reproduces the stale local monitor on the previous code and passes with the fix. Checks cover local/remote stale displays, renumbering, fullscreen resolution changes, offline PCs, remote-only servers and restoring an excluded monitor. Tests do not change user settings or inject input. A Stream Deck button that still selects an old display ID needs its current monitor selected again.

Previous release: 0.7.25

- Fixed screenshot clipboard sharing: Windows screenshots and copied PNG/bitmap images now transfer over KVM and can be pasted on the other PC with Ctrl+V. The previous implementation handled text and Explorer file copies but did not publish clipboard images.
- Images use lossless PNG at their original dimensions, transferred in small chunks over the separate file channel. Recipients publish native bitmap/PNG clipboard formats and a file fallback; no extra mouse or keyboard input is sent. Update both PCs for native image pasting.
- A temporarily busy Windows clipboard is retried without sleeping on the UI thread. New copies made during an active transfer are checked again afterwards, and the receiving clipboard is retained until the transfer succeeds.

Validation: screenshot/PNG capture and a 4.9 MB image transfer, pixel and SHA-256 checks, native clipboard formats, existing nested/empty-file transfers and invalid image rejection. Windows CI additionally checks persistence after the image payload is disposed. The live gaming installation and clipboard are kept intact during local tests.

Update through **Updates → Update all PCs**. Restarting Windows is unnecessary.

Previous release: 0.7.24

- Fixed the Updates window retaining an older “already installed” result after a newer release becomes available. Each connected PC now shows its reported installed version and whether it needs the currently checked release.
- Peer update progress follows the language of the viewing PC, including progress from older Russian-language clients. Late messages from previous requests cannot replace the current result. A peer reporting an older installed version cannot incorrectly skip the update as already current.
- Connected PC rows refresh after reconnection and language changes. Existing pairing and older update-capable clients remain compatible.

Validation: regression checks cover 0.7.21 → 0.7.23, repeated requests, late download messages, invalid already-installed replies, English/Russian switching and legacy messages. Update signature, rollback and portable migration checks are retained.

Update through **Updates → Update this PC** to refresh the controlling app, then **Update all PCs**. This release also includes the replay memory reduction from 0.7.23.

Previous release: 0.7.23

- Reduced replay memory by encoding video independently of audio capture. Waiting for local/KVM audio now retains compressed packets; a short raw-frame buffer covers capture jitter. Audio keeps its existing sample clock and arrival allowance. Resolution, FPS, codec and quality settings are preserved.
- Capture allocates RGB16/P010 GPU readback buffers only when that format is used, and releases the startup frame reference promptly.
- Audio-enabled recording now uses one hardware encoder process and one lightweight video-copy/audio-mux process. The additional `ffmpeg.exe` does not run a second video encoder. Shutdown and failures stop both workers with bounded waits.
- Includes the Stream Deck installation fix described below.

Measured with the same moving 1440p120 AV1 Ultra fixture: combined private working set fell from about **525 MiB to 393 MiB (25%)**, including every recording FFmpeg process. DeskGlide's part fell from 278 MiB to 134 MiB. These are isolated recording measurements, not a memory ceiling for the complete app with NDI, KVM viewers and settings open.

Validation: decoded NVIDIA AV1/HEVC/H.264, mixed/separate/silent audio and the longer KVM-audio wait. The 90-second AV1 run used one encoder session, no exhausted frame history and no missing local audio samples. Three consecutive saves retained every available source frame without overlap; reference audio/video timing stayed within 10 ms. AMD H.264/HEVC were also exercised on the local Radeon; physical Intel hardware remains unverified. See HARDWARE-VALIDATION.md for measurements and limits.

Update through **Updates → Update all PCs**, or download **DeskGlide.exe**. Existing settings and pairing are preserved.

Previous update: 0.7.22

- Fixed **Stream Deck → Install plugin** doing nothing when Windows redirects the package file association to `streamdeck://app/openfile`. DeskGlide now passes the bundled plugin package directly to the installed Stream Deck executable, including custom installation paths when Stream Deck is running.
- Plugin 1.0.1 retains the minimal icons, monitor switching, automatic language selection and corrected replay timer. Existing Stream Deck button profiles are preserved.

Validation: reproduced the file-association failure in the installed Stream Deck 7.4.2 and confirmed that direct package activation installs plugin 1.0.1. Added a regression check for paths containing spaces and punctuation; no keyboard or mouse input is injected by this check.

Previous release: 0.7.21

- Fixed an update/exit freeze when the NDI runtime stalls while destroying a sender or receiver. Shutdown now has a deadline and keeps native resources alive until their owning worker finishes. An existing, unowned single-instance mutex no longer prevents a replacement from starting.
- Reduced replay memory: frames are packed once into NV12, consumed frames are released promptly, and native replay buffers have a bounded reuse cache. NDI frame pooling is bounded across threads. Recording-only capture avoids allocating unused RGB/P010 readback arrays.
- Added frame-lifetime/shutdown regression checks and capture diagnostics distinguishing desktop updates from accumulated/skipped frames.
- Documented AV1 playback requirements. The reported Phasmophobia slow motion was reproduced with the installed MPC-HC 1.8.8/LAV 0.74 decoder: 10 seconds took 21.2 seconds to decode (56.6 FPS). D3D11 decoded the original at about 229 FPS; the same footage in HEVC played normally for the user. Update MPC-HC/LAV on playback PCs to hardware-decode AV1.

Real gameplay verification: an isolated AV1 High 1440p120 recorder ran alongside the installed utility for 90 seconds. Its working set stayed about 212–284 MiB, with no missing local audio samples or exhausted frame history. This is a replay-only process measurement, not a RAM ceiling for all enabled features. The game/capture supplied fewer than 120 fresh images per second; configured output FPS is not proof of 120 distinct game frames. See HARDWARE-VALIDATION.md for checks and limits.

Update through **Updates → Update all PCs**, or download **DeskGlide.exe**. Settings, pairing and virtual devices are preserved. The currently running installation is not replaced during gameplay testing.

Previous release: 0.7.20

- Redesigned the Stream Deck page with a compact install button, an inline live connection indicator and three clear setup steps. Removed the redundant download button.
- Cleaned up KVM pairing: the computer name is separate, the pairing code is hidden by default, and reveal/copy controls sit inside the code field. Copy works without exposing the code.
- Both pages support English and Russian.
- Stream Deck plugin 1.0.1 adds **Switch Monitor**, with a choice of monitors from your KVM layout. It works directly through KVM, including when mouse edge transitions are locked.
- Replaced key artwork with minimal white icons and distinct enabled/disabled/locked states. Removed the computer count from Open KVM and the language selector; the plugin follows Stream Deck's language.
- The replay timer includes written frames in the current segment, instead of jumping at two-second segment boundaries. Fresh recorder snapshots allow bounded interpolation; stalled/offline recording stops advancing. The recording/export pipeline is unchanged.

Validation: Windows CI checks the actual WPF controls, code reveal/hide/copy, existing Settings/KVM/tray actions and the single-file build. Both redesigned pages are rendered in English and Russian. The installed utility and profile are preserved during development.

Update through **Updates → Update all PCs**, then use **Settings → Stream Deck → Install plugin** to update the plugin. Plugin 1.0.1 requires DeskGlide 0.7.20 for monitor switching; the previous 1.0.0 Marketplace submission is still pending review.

Previous release: 0.7.19

- Added a Stream Deck plugin with Save Replay, Open KVM, Lock Mouse Transitions, Toggle Replay and Toggle Streaming. Buttons show buffer fill, connected computers and actual clip-delivery confirmation.
- Added **Settings → Stream Deck** with one-click plugin installation, download/setup links and live connection status. The package is embedded in the portable EXE; it does not create a folder beside the app.
- A Stream Deck on a receiving PC can control replay and streaming on its paired controller PC. Commands use the existing authenticated KVM channel. Disconnected/failed commands show an alert and are never resent automatically.
- Supports English/Russian action settings. Requires Stream Deck 7.1+, DeskGlide 0.7.19+ on participating PCs and the same Windows user session for the local plugin.

Install the plugin through DeskGlide or download **com.yami.deskglide.streamDeckPlugin** from this release. [Setup](https://github.com/Aleksei-Melnik/DeskGlide/blob/main/docs/STREAM-DECK.md).

Validation: official Elgato CLI packaging/validation, real Windows named-pipe tests, official SDK integration through a simulated Stream Deck WebSocket, disconnect/reconnect and duplicate-request checks, and isolated paired-network tests. Existing Windows UI/portable tests run in CI. A physical Stream Deck key press and a two-PC user workflow still need verification. The installed gaming copy and profile were preserved during development.

Marketplace status: plugin 1.0.0.0 was submitted under **Yami (@yami)** on October 12, 2026. Maker Console shows **Pending review**. Automatic publication after approval is enabled. This release is available through GitHub while Elgato reviews the submission; Marketplace approval has not been granted yet.

Previous release: 0.7.18

- Open Settings with one left click on the tray icon. Right click continues to open its menu; double-clicking does not create another window.
- Fixed the discarded first right click after startup: the menu is populated before the tray icon appears, and its opening state is evaluated after refresh.
- Enabled WinForms menu message processing in the WPF application from startup, before opening any KVM viewer. This restores the menu's normal keyboard dismissal and message filtering.

Validation: the Windows CI exercises real NotifyIcon callbacks under the production WPF message loop, reproduces the previous first-click failure, and checks first/repeated left and right clicks, menu actions, Escape dismissal and the single-file build. Existing WPF Settings/KVM tests also run. Live game input and installed profiles are left untouched.

Update through **Updates → Update all PCs**, or download **DeskGlide.exe**. Pairing, recording settings and virtual devices are preserved.

Previous release: 0.7.17

- Improved replay stability under CPU/GPU pressure. Video uses a direct NV12 path with 25% fewer pipe bytes, bounded frame history and dedicated video/audio writers that wake without waiting for normal-priority thread-pool callbacks.
- Improved recording audio continuity: larger WASAPI capture buffers and a shared, delayed recording clock give complete packets time to arrive. NDI transmission keeps its existing timing.
- A successful Save starts a new unsaved interval. The next clip contains only new footage since the previous Save, up to the chosen duration. Failed exports retain unsaved footage; saving does not restart the encoder. Updates, shutdown and settings changes still do not export clips automatically.
- Fixed frame boundaries when assembling clips with AAC audio. Export keeps decoding preroll without shifting the visible video or skipping frames between consecutive saves.
- The quality dropdown now explains Ultra, High, Medium and Low and shows the NVIDIA bitrate ceiling for the selected resolution and FPS. Labels update with the settings and support English/Russian. AMD/Intel use quality targets; their bitrate is not a fixed ceiling.

For 1440p120, start with **AV1 + High** on hardware that supports it, or HEVC + High. High's NVIDIA ceiling is 80 Mbps; Ultra is 120 Mbps. For 1080p60, High's ceiling is 23 Mbps. H.264 remains available for broader player/editor compatibility. The [quality guide](https://github.com/Aleksei-Melnik/DeskGlide#instant-replay) explains every level.

Validation: NVIDIA AV1/HEVC/H.264 and AMD H.264/HEVC passed decoded recording checks at 60 and 120 FPS under artificial CPU/GPU pressure, including audio continuity and consecutive saves. A long HDR AV1 1440p120 test passed without losing available source frames or accumulating A/V drift. The exact portable EXE and GitHub's interface/build tests passed too. [Results and limits](https://github.com/Aleksei-Melnik/DeskGlide/blob/main/HARDWARE-VALIDATION.md): these checks do not establish zero game-FPS overhead, every GPU generation, physical Intel support or 4K/8K performance.

Update through **Updates → Update all PCs**, or download **DeskGlide.exe**. Existing profiles, pairing, camera setup and recording tools are preserved. No files need to be copied beside the portable executable.
