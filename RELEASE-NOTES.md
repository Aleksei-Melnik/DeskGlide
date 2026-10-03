ScreenCapture 0.6.0 (formerly SdrCapture)

- NDI audio volume: 0–100%, default 50%. Replay audio remains independent.
- Public GitHub releases with signed manifests, verified package/file hashes,
  retained settings, replay saving before restart and file/startup rollback.
- Update this PC or all connected compatible KVM PCs from the controlling PC.
- Separate KVM window with connected PCs; one viewer per remote PC, monitor
  selection, fullscreen and view-only toggle. Launch directly with `--kvm`.
- Settings navigation with descriptions, contextual tips, role-specific KVM
  fields and a short page transition respecting Windows animation preferences.

NDI source name and SDR processing are unchanged. Existing shortcuts can keep
using SdrCapture.exe; ScreenCapture.exe is the new primary launcher.

**First upgrade from 0.5.x:** exit the app and unpack this ZIP over the existing
app folder on each PC, retaining tools/. After this one-time installation,
subsequent releases can be installed from the app. Offline PCs must reconnect.
NDI runtime and FFmpeg are installed separately (see README); existing tools
are preserved. Release signatures are not Windows Authenticode signatures.

Validation: local build; signed-manifest/tamper/path tests; interrupted-copy
rollback; audio-clock tests; paired local KVM protocol tests; synthetic NDI
volume/mute test. Real two-PC update coordination and Discord listening still
require validation on connected machines.
