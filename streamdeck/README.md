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
