DeskGlide 0.7.13

- Replaced replay duration fields and presets with one slider: 1 minute to 2 hours, in one-minute steps. Its current value follows the selected language. Changing duration does not restart recording.
- Removed the native caption and system-button styles that painted a faint second set of minimize/close controls beneath the custom header. Our working controls remain. Minimize/restore and KVM fullscreen use the same borderless window.
- Existing settings, save sound, corner protection, SDR streaming and single-EXE packaging are retained. Old durations shorter than a minute use the new one-minute minimum.

Use Updates → Update all PCs, or download the single DeskGlide.exe. Existing settings, pairing, camera setup and recording tools are preserved.

Validation: Windows CI covers slider range, saved values, live localization and legacy profiles; absence of native caption/button styles, including after style changes, minimize/restore and KVM fullscreen; normal and single-EXE regressions for UI, KVM, updater, camera and audio. No UI, input or sound playback tests run on the gaming PC during development.
