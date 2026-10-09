DeskGlide 0.7.11

- One portable DeskGlide.exe: recording tools, settings and other components stay in the Windows user-data folder. No tools folder or shortcuts are created beside the executable.
- Old tools folders migrate into %LOCALAPPDATA%\SdrCapture\RecordingTools in the background. Copies are checked before originals are removed. Unrelated user files are preserved; a busy encoder is cleaned up on a later launch. The verified current FFmpeg cache takes priority.
- Removed the gray window outline, including after leaving KVM fullscreen. Clicking the selected settings tab preserves its content, scrolling and current transition.

Use Updates → Update all PCs, or download DeskGlide.exe. No running app needs to be restarted while you are playing.

Validation: recording-tool migration, cancellation, byte preservation, cache selection and unknown-file preservation; Windows CI checks UI navigation, window borders, KVM fullscreen and both normal and single-EXE builds.
