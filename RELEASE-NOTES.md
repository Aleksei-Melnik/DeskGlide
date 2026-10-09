DeskGlide 0.7.12

- Replay duration is now editable: 30 seconds to 2 hours. Enter minutes and seconds or use a quick preset. Existing durations are preserved. Changing duration or notification preferences does not restart the encoder.
- Added a soft, ascending two-note save chime, inspired by Mea. It plays after the clip reaches the chosen folder, with a toggle, volume control and preview in Instant replay. The default volume is 25%. No audio files are created beside the executable.
- Long replay buffers are retained for the configured duration rather than cut off at 32 GB. Export and network-copy timeouts account for clip size. Sufficient local disk space is still required.
- Window corners now use one native rounded silhouette, without an overlapping WPF/GDI clip. Native outline suppression is reapplied after activation, theme changes and KVM fullscreen.
- Added Block switching near screen corners under KVM & pairing. Off by default; when enabled, the 32-pixel corner areas block mouse transitions in both directions. Monitor shortcuts remain available.
- The tray tooltip now says Streaming and Replay, localized to English or Russian.

Use Updates → Update all PCs, or download the single DeskGlide.exe. Existing settings, pairing, camera setup and recording tools are preserved.

Validation: Windows CI covers normal and single-EXE builds; duration limits and legacy profiles; silent waveform checks and successful/failed clip delivery; WPF editing and navigation; native window border policy; local/remote corner geometry; updater, camera, audio-clock and KVM regressions. No UI, keyboard-input or sound playback tests run on the gaming PC during development. Actual two-hour GPU recordings are not part of CI.
