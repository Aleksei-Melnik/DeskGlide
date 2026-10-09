DeskGlide 0.7.14

- Settings, the KVM computer picker and Updates now appear with a short, eased 200 ms fade. Their controls are available throughout the transition.
- Restoring these windows through the tray also uses the transition. Bringing an already visible window forward does not replay it. Closing or minimizing during a fade clears the animation immediately.
- Windows' disabled-animation preference is respected. Native rounded corners, borders and caption controls are preserved. The remote-video viewport is not animated.
- The remote KVM screen can now be resized independently in width and height by dragging any edge or corner. Video keeps its aspect ratio, the toolbar wraps in narrow windows, and F11 restores the size chosen before entering full screen.

Use Updates → Update all PCs, or download the single DeskGlide.exe. Existing settings, pairing, camera setup and recording tools are preserved.

Validation: Windows CI exercises opening, repeated opening, restore and close during animation for all three windows, remote-viewer resize hit testing, embedded viewport resizing, toolbar wrapping and full-screen size restoration, alongside existing UI, KVM, update, audio and camera checks in normal and single-EXE builds. No UI or input tests run on the gaming PC during development.
