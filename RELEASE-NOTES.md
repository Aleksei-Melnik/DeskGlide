DeskGlide 0.7.8 refreshes the window frame and settings navigation.

- The native Windows title bar is replaced with integrated minimize and close controls. Drag the app header to move a window. Settings keep their fixed size.
- Rounded window corners and the existing dark glass palette are shared by Settings, KVM, Updates and backup dialogs.
- Monitor removal uses clean vector icon buttons with hover and keyboard-focus feedback. Removing a display returns it to Available monitors and does not disconnect its computer.
- Settings pages fade and slide in, while navigation highlights transition smoothly. Rapid clicks replace the current animation immediately and preserve unsaved edits. Windows' reduced-motion preference is respected.
- The KVM viewer uses the same header; F11 hides it in full screen and restores it on return.
- Restart DeskGlide is available in the tray menu. The replacement waits for the current process to fully exit and reloads saved settings. Restart does not export a replay clip.

Validation: native Windows hit-testing confirms the header can move the window while buttons receive normal client input, without a reserved system title bar. UI tests cover minimize/restore, close without saving, navigation and Save, rapid repeated transitions, language changes, individual display removal, embedded KVM keyboard input and full-screen return.
Restart tests use isolated processes and a separate singleton; they verify that the replacement waits for shutdown and rejects stale parent identities without starting capture or sending keyboard input.

Update from Updates → Update all PCs. Capture, SDR conversion, recording and network protocols are unchanged.
