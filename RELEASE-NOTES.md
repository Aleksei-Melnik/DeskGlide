DeskGlide 0.7.19

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
