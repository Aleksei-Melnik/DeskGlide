# KVM keyboard validation — 0.7.32

The reported Windows key could act on both computers; other system shortcuts
acted locally or never reached the server. The viewer used form KeyDown/KeyUp
events independently of the low-level controller. Those events cannot suppress
the local shell's handling of system shortcuts. Viewer keyboard routing now
uses the dedicated controller/helper; form handlers do not send a second copy.

## Automated checks

`--kvm-input-tests` exercises the actual low-level callback with controlled
transport/focus/native-injection seams. It installs no global hooks, injects no
OS input and opens no live KVM connection. Every DOWN and UP of these chords
must return the suppression result and produce exactly one ordered remote
message with its original scan/extended flags:

- Ctrl+Shift+Esc
- Alt+Tab and Alt+F4
- Ctrl+Esc and Shift+4
- Left Windows+R and right Windows+E

Additional checks cover accessibility/Stream Deck injected events, autorepeat,
rejection of KVM's own marked input, unchanged viewport refresh, loss of focus
while Win is held, re-entry without reconfiguration, Alt held during a server
handover, emergency return and modifier cleanup, screen-edge locking, foreign
window rejection and protected-desktop release. Existing tests retain accepted
injection tracking, failed-release retries, private SID/PID-bound IPC, malformed
frames, bounded queues, parent heartbeat and child shutdown.

Native ownership checks create private hidden Form/PictureBox HWNDs without
showing or activating them. They verify root/child ownership, wrong-process and
invalid-window rejection, and refusal to capture a hidden background viewer.
The CLI restores its prior synchronization context after this fixture so the
following async pipe tests do not wait on an unpumped WinForms context.

Local builds have zero warnings/errors. Input, update, recording cadence and
recording duplicate regressions pass. The signed-release workflow runs its full
folder/portable/package suite against the final release commit before publishing.

## Boundaries and physical verification

No keyboard shortcuts were sent into the user's running programs during these
checks. Actual focused WPF/WinForms interop, shell behavior and the two physical
PCs therefore still need an interactive check after updating. Keep Control
enabled and focus the remote desktop image. Verify Win+R, Alt+Tab, Alt+F4 and
Ctrl+Shift+Esc using disposable applications on the remote PC; the Host should
stay in the viewer. Then leave the viewer with a modifier held and release it:
neither PC should retain a modifier. Verify Control off, toolbar editing,
switching viewers, fullscreen, disconnect and Ctrl+Alt+Esc return.

Ctrl+Alt+Esc is the deliberate local escape, F11 is viewer fullscreen, and the
configured KVM launcher/edge-lock shortcuts remain local. Windows protected
desktops and administrator input approval keep their existing boundaries;
this change does not implement Ctrl+Alt+Delete or bypass Windows consent.
