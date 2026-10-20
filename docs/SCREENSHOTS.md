# Local screenshots (0.7.33)

Press **PrintScreen**, drag an area, then annotate or export it. Screenshots are
also available from the DeskGlide tray menu. Configure the shortcut and folder in
**Settings → Screenshots**. The default is the current user's Pictures folder,
under `DeskGlide/Screenshots`. PNG is the only exported file format. There is no
upload service, public link, online image search or HDR-file export.

The editor provides freehand pen, line, arrow, rectangle, translucent marker,
multiline text and opaque redaction. Its implementation, layout and graphics
are original DeskGlide work; it does not contain Lightshot code, assets or branding.
It offers common screenshot workflows rather than a claim of complete Lightshot
service compatibility or legal clearance.

## Controls

| Action | Shortcut |
| --- | --- |
| Capture | PrintScreen |
| Copy bitmap and PNG | Ctrl+C or Enter |
| Save a unique PNG to the configured folder | Ctrl+S |
| Choose a destination | Ctrl+Shift+S |
| Print through the Windows dialog | Ctrl+P |
| Undo / redo annotations | Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z |
| Select the monitor under the pointer | Ctrl+A |
| Move selected area | Drag inside it with Select, or arrow keys |
| Move by ten pixels | Shift+arrow |
| Resize selected area | Drag an edge/corner handle |
| Copy immediately after selecting | Hold Ctrl while selecting |
| Commit multiline text | Ctrl+Enter or choose another tool |
| Cancel text / cancel capture | Escape |

Right-click returns to Select. Canceling leaves the clipboard and filesystem
unchanged. A successful copy or save closes the editor. Alt-tabbing out cancels
the overlay; owned save/color/print dialogs are exempt. Display geometry changes
cancel the frozen canvas. Redaction overwrites pixels in the flattened PNG and
clipboard image; it is not a reversible blur or a saved editable layer.

## HDR and physical pixels

Every attached DXGI output is acquired on its own adapter with
`IDXGIOutput5.DuplicateOutput1`. HDR outputs must supply FP16 linear scRGB;
there is no clipped 8-bit GDI fallback. SDR BGRA retains its original code values.
The screenshot path is independent from the recording/NDI UYVY conversion and
does not subsample chroma, resize frames, stop capture or switch Windows HDR.

For each HDR monitor, linear scRGB is scaled by `80 / WindowsSdrWhiteNits`.
The same hue-preserving fixed highlight shoulder used by the established
DeskGlide color pipeline bounds the result; sRGB encoding follows. PNG contains
an explicit sRGB chunk. This is an SDR representation of an HDR desktop, not an
HDR file and not a promise that every scene matches the display's HDR appearance.
HDR highlight contrast cannot all fit into an ordinary SDR PNG.

The frozen editor uses physical virtual-desktop coordinates, including negative
origins. DXGI's unrotated images are rotated into display orientation before
selection; 90/180/270-degree monitors are supported. The crop has native pixels
regardless of Windows UI scaling. The combined bounding canvas is limited to
40 million pixels; large gaps between monitors count toward that bound. A
capture failure is reported rather than silently substituting a washed-out image.
Monitors are acquired sequentially, so moving content across monitors is not a
single synchronized presentation.

## Input and storage

PrintScreen uses `RegisterHotKey` with no-repeat. It runs after the existing KVM
low-level hooks: forwarded/suppressed input is not also used on the Host. The
receiving PC can handle the injected key using its own registration. While the
editor is open, seamless edge switching is suspended and restored on close.
No new hook, elevated helper command or network screenshot command is introduced.
PrintScreen must be available: close another app that owns it; if Windows screen
snipping intercepts it, adjust that Windows keyboard preference. DeskGlide never
changes that registry setting. The tray command remains available.

Files are encoded to a unique temporary file beside the chosen destination,
flushed, then renamed atomically. Failure removes the temporary file and leaves
an existing destination intact. No temporary data is written to the root of C:.
Settings and encrypted backup/restore include screenshot preferences; old
profiles receive defaults. Captured pixels remain local.

## Validation

`--screenshot-tests` uses synthetic SDR/FP16 tiles and never registers keys,
activates a window, captures a live desktop or writes the OS clipboard. It checks
all SDR code values, sRGB-tagged PNG decode, HDR white levels 80/200/480 nits,
monotonic highlights, nonfinite values, mixed-display/negative-origin crops,
all rotations, annotations, flattened redaction, clipboard payload formats,
undo/redo, atomic-save rollback and legacy preferences. It renders original
English/Russian editor previews without showing a window.

`--screenshot-ui-tests` operates a real synthetic editor window and is guarded
against a running DeskGlide session. It checks reverse selection, move/resize,
arrow annotation, undo/redo, pixel nudge, text cancellation and toolbar bounds.
CI runs both tests in folder, standalone portable and signed release packages.

`--screenshot-capture-test` performs a real DXGI capture and PNG round trip,
reports only dimensions/HDR/white level/encoded size and retains no desktop
image. The local HDR 2560×1440 display at 240-nit Windows SDR white passed.

Physical PrintScreen delivery, Windows Snipping Tool/other app conflicts,
two-PC activation, actual clipboard paste into third-party apps, physical
mixed-DPI layouts and printer dialogs still require interactive verification.
Synthetic checks do not establish perceptual color accuracy for every HDR game.

Technical references: [Microsoft HDR capture guidance](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture),
[DXGI rotation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api),
[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).
