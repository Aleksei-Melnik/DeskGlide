ScreenCapture 0.7.2 improves KVM viewing, shortcuts and Discord audio setup.

- KVM viewing offers 30 / 60 FPS. GPU desktop capture replaces slow screenshots where supported. SIMD JPEG with full 4:4:4 colour uses a separate authenticated TLS connection, bounded to two unacknowledged frames. Decoding runs outside the UI thread and retains the newest frame. Actual FPS is displayed. Update both PCs; older peers retain the slower compatible mode.
- Quality: native JPEG 95 for text, JPEG 88, or economical 1080p. Portrait rotation is handled on the GPU. Unsupported environments fall back to GDI; GPU load and network bandwidth affect actual FPS.
- Stream Deck software-generated keys now work for KVM shortcuts. Holding a shortcut no longer repeatedly toggles it. Configure Open KVM (Ctrl+Alt+K) and Block/unblock mouse transitions (Ctrl+Alt+Pause) under KVM / network. Monitor shortcuts remain Ctrl+Alt+F1–F12. Save settings after editing.
- Reopening KVM restores hidden/minimized/off-screen windows. Settings no longer keep other app windows disabled by a permanent modal dialog. Shortcuts hand foreground permission to the existing instance.
- Routine clipboard/file-transfer progress and success balloons are removed. Errors and replay-save notifications remain.
- Discord reception separates network audio through a cable, a direct local input/mixer (including RØDECaster), and Silent. Other cable endpoints can be selected manually. Direct-input mode does not play incoming NDI audio into a cable.
- «Настроить устройства…» registers the camera and requests administrator permission separately to rename the selected recording endpoint, addressing 0x80070005. It does not change default Windows devices, install drivers at startup, or elevate the main app. Names help Chromium grouping; Discord may retain a prior audio choice, so automatic selection is not guaranteed.

Validation: authenticated TLS loopback viewing at 2560×1440 measured about 30.0 / 60.1 FPS. This is a single-PC measurement, not a LAN guarantee. GPU rotation/pixel tests, SIMD JPEG orientation/colour, physical/software shortcut dispatch and repeat suppression, clipboard/TLS/input order, settings/window rendering, audio endpoint rules and update tests passed. Receiver-side UAC renaming and RØDECaster hardware still need testing on the streaming PC. The working Discord camera and NDI SDR conversion are unchanged.

Direct file dragging remains experimental and was reported not working between the actual PCs. Use Ctrl+C / Ctrl+V; this update does not claim to fix dragging.
