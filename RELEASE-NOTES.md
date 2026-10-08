ScreenCapture 0.7.5 brings a NeuralMea-style WPF interface and fixes native-resolution capture, replay exports and KVM behaviour during games.

- New dark Settings and KVM windows with matte Acrylic on supported Windows 11, translucent cards and pink/purple accents. English/Russian selection is under General. The tray menu remains compact and dark.
- Video and streaming audio controls are grouped together. General contains language, autostart and power. Recording includes 720p alongside native/1080p/1440p/4K. Status uses separate capture, replay and connection cards.
- Virtual devices is hidden on the controlling host. Existing audio cables are reused and their installation action is hidden. Backup/restore, codecs, tracks, shortcuts, pairing and updates are retained.
- A monitor can be removed individually with × and restored from Available monitors. Remote servers remain accessible through KVM without participating in edge switching.
- Removed desktop-edge drag windows that caused the visible screen strip. File copy/paste remains available; direct cross-PC dragging is not implemented in this version.
- Replay exports only on explicit Save or its shortcut. Update/restart no longer silently saves clips. Unsaved buffers are discarded; already requested network deliveries can still complete later.
- Streaming uses the monitor's preferred native canvas. A 1280×1024 fullscreen game stretches across it. The virtual camera now receives native dimensions, with frame capacity through 7680×4320 instead of fixed 1080p.
- Preserve SDR shadow brightness rather than applying an extra darkening transfer curve. GPU tests verify all 256 gray levels, black/white endpoints, HDR bounds and cursor colours.
- Reduced display-brightness queries from twice per frame to four times a second; capture and sender retain dedicated threads and bounded buffering. The camera keeps its last valid frame during source recovery instead of turning black after two seconds.
- Remote KVM movement uses physical mouse deltas, ignoring game cursor warps. Fullscreen resolution changes no longer detach the edges of a saved desk layout; cursor coordinates scale between logical and physical sizes.
- Open clips folder handles UNC destinations through Explorer without blocking the tray.

Validation: WPF production dispatcher tests exercise navigation/Save, live language switching, reopening, delayed device discovery and embedded KVM keyboard release/fullscreen. Native camera probes verify 1440p60 with changing frames, RGB24/32, 30 FPS negotiation, pause recovery, and Full HD/1440p/4K/8K/ultrawide/portrait formats. HEVC NVENC lifecycle tests verify zero exports before Save and no additional clips on reconfiguration, disable or shutdown. GPU tests verify stretching and SDR/HDR colour bounds. Four local fullscreen transitions recovered fresh capture while the sender stayed alive. HEVC replay cadence fixtures at 1440p60 and 1440p120 had zero repeated or skipped frame IDs after warmup. The actual Aurora recording share opened in Explorer.

After updating, fully quit and reopen Discord/OBS on receiving PCs to load the new camera DLL and negotiate native resolution. Existing settings and recording tools are kept.

Game-load frame rate, physical 8K60 capture, remote Discord delivery and the reported Phasmophobia behaviour still need confirmation on the affected equipment. Local fixtures do not prove zero latency or fresh 60 FPS during every game.
