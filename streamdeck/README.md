# DeskGlide Stream Deck plugin development

Official Elgato Node SDK 3.0.2, manifest SDK 3 / Node 24 / Stream Deck ≥7.1.

```
pnpm install --frozen-lockfile
pnpm run build
./scripts/icons.ps1
pnpm test
pnpm validate
pnpm pack
```

`../scripts/Build-StreamDeck.ps1` performs build, tests, validation and packing.
Set `DESKGLIDE_TEST_EXE` to a freshly built DeskGlide executable to run the official
SDK/WebSocket/private-pipe integration test. Its fixture does not initialize capture,
KVM input, clipboard, hotkeys or the installed user profile.

The UUID remains stable after release. Distributed files are immutable; all SDK logs
use the SDK's runtime log directory. No bundled manifest is read by our application code.
Do not hardcode a machine name, pairing code, user email or Marketplace credentials.

Version 1.1 adds the separate **Video Recording** action (`com.yami.deskglide.recording`).
It sends `toggleRecording` through the same private local/paired-controller bridge.
The selected DeskGlide app owns the recording profile, encoding PC and folder;
the button cannot supply a destination path. Recording/start/save/error labels
follow live state and support English/Russian. `recordingSupported` distinguishes
older app snapshots from a stopped recorder. Recording operations have a 600-second
reply deadline and are never retried automatically after uncertainty or disconnect.
