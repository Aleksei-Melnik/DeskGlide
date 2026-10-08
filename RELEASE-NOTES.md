ScreenCapture 0.7.6 fixes instant replay failing to start when recording tools are missing from a fresh or updated installation.

- Enabling replay automatically downloads the pinned FFmpeg package, verifies its SHA-256 and installs the encoder and probe together.
- Recording dependencies are kept in the Windows user's local data folder so they remain available after moving or extracting the app. Existing portable tools installations are supported.
- The Instant replay page shows a setup button when tools are missing, with download progress and a retry action. Setup needs internet access on its first run and no administrator permission.
- Incomplete installations are repaired; unrelated files are preserved. Failed or cancelled downloads leave no partial executable pair. The streaming sender continues running during setup.
- Recording retains HEVC/H.264/AV1 NVENC and saves clips only on explicit Save or its shortcut.

Validation: real network download, pinned checksum, both installed executables and NVENC encoder availability checked. Regression tests cover missing probe, shared-cache fallback after moving the app, bad checksum, cancellation, incomplete archives, safe extraction and preservation of unrelated files. WPF navigation, Save and language-switch tests passed. A HEVC NVENC replay test used the shared tools and confirmed no export before explicit Save, and no extra export on reconfiguration, disable or shutdown.

Update from the app's Updates page. Existing recording settings and saved clips are retained.
