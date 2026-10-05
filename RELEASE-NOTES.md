ScreenCapture 0.8.0 — SDR streaming, instant replay and KVM in a new English WPF interface.

- Settings, the computer chooser, remote-viewer controls, update manager and profile password dialogs now use WPF.
- NeuralMea-inspired dark surfaces, pink/purple accents, rounded controls and short page/hover animations. Motion respects Windows animation preferences.
- Settings navigation groups capture/replay, computers and application preferences. The tray menu puts Settings and KVM first, followed by streaming/replay toggles and clip actions.
- English labels, instructions, notifications and application-generated error messages throughout. Device names and user content retain their original language.
- The existing native KVM video/input surface is hosted inside WPF. NDI, SDR conversion, recording, network protocols, camera formats and existing settings remain compatible.
- Device discovery remains asynchronous. Missing-device selections and edits made while loading are preserved. Windows remain fixed-size and can be minimized.
- The self-contained package includes the WPF runtime. No additional UI runtime installation is required.

Validation: rendered 27 settings views across Off/Host/Client roles; exercised delayed device discovery, selection persistence, close/reopen, monitor placement and remote-only layout preservation. Tested native keyboard down/up and F11 repeat handling inside the WPF viewer. Feature, update integrity, KVM protocol/video and audio timing checks passed locally. End-to-end behaviour on the user's other PCs still needs confirmation after updating.
