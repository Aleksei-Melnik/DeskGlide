DeskGlide 0.7.9 is distributed as one portable DeskGlide.exe.

- The Windows Desktop runtime, KVM JPEG library, virtual camera and dependency notices are bundled. No separate .NET installation or libraries beside the EXE are required.
- Camera resources and diagnostic files use the existing per-user data folder; native runtime files use Windows' internal bundle cache. Recording tools keep their existing automatic, verified setup.
- Update all PCs migrates older folder installations to a single executable. The active launcher name, settings, pairing keys, camera names, shortcuts, recording tools and saved clips are preserved. Only known package files are removed; unknown user files stay.
- Subsequent updates download just the EXE, check its signed SHA-256 and retain startup acknowledgement and rollback. Migration failures restore the old components.
- Existing autostart preferences follow the portable executable you launched.

Download DeskGlide.exe and run it. Existing users can use Updates → Update all PCs. NDI Runtime and the optional VB-CABLE audio driver retain their existing setup requirements; drivers still require Windows permission.

Validation: isolated single-EXE startup with no adjacent DLLs, embedded camera extraction and repair, native camera loading, KVM JPEG encoding, bundled notices, signed portable artifact validation, migration/rollback and user-file preservation. Windows CI also checks the UI, restart, audio timing and KVM for the portable build. Capture, SDR conversion and transport algorithms are unchanged.
