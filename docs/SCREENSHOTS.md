# Local screenshots (0.7.37)

Press **PrintScreen**, drag an area, then annotate or export it. Screenshots are
also available from the DeskGlide tray menu. Configure the shortcut and folder in
**Settings → Screenshots**. The default is the current user's Pictures folder,
under `DeskGlide/Screenshots`. PNG is the only exported file format. There is no
upload service, public link, online image search or HDR-file export.

The editor provides freehand pen, line, arrow, rectangle, translucent marker,
text and opaque redaction. Compact vector-icon tools sit to the right of the
selection, with copy/save actions below its border. Both follow the region,
flip sides at display edges and remain usable for a full-monitor selection.
Crop handles remain available while a drawing tool is selected.
Its implementation, layout and graphics
are original DeskGlide work; it does not contain Lightshot code, assets or branding.
It offers common screenshot workflows rather than a claim of complete Lightshot
service compatibility or legal clearance.

Since 0.7.37, the primary tools occupy a 28×220-pixel vertical bar at 100% scale,
with 22-pixel buttons. The bottom bar is 100×28 pixels: Copy, Save, More, Close.
More contains redaction, stroke width, redo, Save as and printing. Toolbar
scaling is capped at 1.15 and reapplied after DPI changes; buttons stay within
26 physical pixels in tests spanning 96–288 DPI. The screenshot crop retains
its native pixel dimensions.

## Controls

| Action | Control |
| --- | --- |
| Capture | PrintScreen |
| Copy bitmap and PNG | Copy button beside the area |
| Save a unique PNG to the configured folder | Save button beside the area |
| Choose a destination | More → Save as |
| Print through the Windows dialog | More → Print |
| Undo / redo annotations | Ctrl+Z or Undo at the right / More → Redo |
| Redaction / stroke width | More → Redact / Stroke width |
| Move selected area | Drag inside it with Select |
| Resize selected area | Drag an edge/corner handle |
| Commit text | Enter or choose another tool |
| Cancel text / cancel capture | Escape |

Right-click returns to Select. Canceling leaves the clipboard and filesystem
unchanged. A successful copy or save closes the editor. Alt-tabbing out cancels
the overlay; owned save/color/print dialogs are exempt. Display geometry changes
cancel the frozen canvas. Redaction overwrites pixels in the flattened PNG and
clipboard image; it is not a reversible blur or a saved editable layer.

Ctrl+Z undoes annotations even with toolbar focus; while editing text it remains
native text undo. The other retired editor combinations remain disabled.
PrintScreen opens capture directly, without a selection-hint banner. Settings shows only
the PrintScreen preference and storage location; HDR processing is automatic.

## HDR and physical pixels

Every attached DXGI output is acquired on its own adapter with
`IDXGIOutput5.DuplicateOutput1`. HDR outputs must supply FP16 linear scRGB;
there is no clipped 8-bit GDI fallback. SDR BGRA retains its original code values.
The screenshot path is independent from the recording/NDI UYVY conversion and
does not subsample chroma, resize frames, stop capture or switch Windows HDR.

Since 0.7.35, a short-lived ordinary child process acquires screenshot pixels.
Windows rejects a second duplication of the same output in one process with
`E_INVALIDARG`; attempting it in the tray process made both screenshot commands
fail while replay/NDI already owned that output. The child returns raw SDR/FP16
tiles through inherited anonymous stdout, then exits. The parent validates tile
counts, geometry, rotation, color format and byte lengths before building the
preview. Total tile pixels and the combined canvas are each bounded to 40 million
pixels. A 20-second timeout/cancellation terminates the child. No temporary desktop
image, network listener, named public IPC endpoint or new elevation is needed.

A successful `AcquireNextFrame` may contain only a pointer update, with
`LastPresentTime == 0`. A newly created duplication has no previous image to
reuse, and this returned a completely empty texture during local reproduction.
Capture now releases and skips those events, waiting up to five seconds for a
desktop presentation. The receiving process also validates a positive present
timestamp. Pixel brightness does not determine validity: an actual presented
black screen remains capturable. Windows-protected content remains protected.

The editor caches an opaque dimmed bitmap beside the original preview. It
restores changed areas by opaque native-pixel blits and redraws annotations
within the invalid region. Crop motion invalidates changed shade strips,
outlines and labels; `GetUpdateRgn` preserves disjoint strips through WinForms
double buffering. Pending freehand points grow in place and are frozen when
the stroke ends. None of the shade/UI caches is used in PNG or clipboard output.

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
A capture failure shows a visible dialog even when Windows hides tray balloons.
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
It also transfers synthetic mixed SDR/HDR pixels through the production worker,
checks process error/cancellation, and rejects malformed, truncated and trailing
pixel messages.
It distinguishes missing desktop-presentation metadata from valid black
content, checks repeated DPI changes and compares incremental pen/crop frames
against complete redraws. Offscreen pointer tests create no native window and
do not take OS mouse capture or focus.

`--screenshot-ui-tests` operates a real synthetic editor window and is guarded
against a running DeskGlide session. It checks reverse selection, move/resize,
arrow annotation, undo/redo, text cancellation and toolbar bounds.
It also verifies visible Undo/Redo buttons, Ctrl+Z and absence of retired copy/save keyboard
combinations, resize handles while drawing and compact bar bounds. CI runs both
tests in folder, standalone portable and signed release packages.
Native tests also force actual window painting and exercise More/Redo without
dismissing the editor.

`--screenshot-render-performance-test` runs the pixel-comparison and compact-DPI
checks at 2560×1440 and records median/p95 paint cost and invalidated area. This
measures offscreen renderer work, not display presentation FPS or end-to-end
input latency.

`--screenshot-capture-test` performs a real DXGI capture and PNG round trip,
reports only dimensions/HDR/white level/encoded size and retains no desktop
image. The local HDR 2560×1440 display at 240-nit Windows SDR white passed.

`--screenshot-concurrent-capture-test` holds a real replay/NDI duplication in
the calling process, reproduces the original second-duplication failure, and
then captures/PNG-decodes through the production worker while continuing to
poll the original capture. It records metadata only and never shows a window,
writes the clipboard or retains a desktop image. This passed on the local HDR
2560×1440 display with the installed DeskGlide session also running. These real
GPU checks are separate from headless hosted CI.

Physical PrintScreen delivery, Windows Snipping Tool/other app conflicts,
two-PC activation, actual clipboard paste into third-party apps, physical
mixed-DPI layouts and printer dialogs still require interactive verification.
Synthetic checks do not establish perceptual color accuracy for every HDR game.

Technical references: [Microsoft HDR capture guidance](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture),
[DXGI rotation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api),
[DXGI duplication limits](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1),
[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).

## Capture and clipboard latency (0.7.37)

The tray prewarms one ordinary same-executable worker with native DXGI objects.
It waits on its inherited anonymous stdin pipe; it does not poll, read back or
save the desktop while idle. PrintScreen sends a request byte and only then
acquires a presented frame. The worker exits after returning that image. Closing
the editor warms the next worker. Pipe EOF and tray shutdown dispose the waiting
worker; a stale duplication after a display/driver change falls back to a fresh
capture for the same request. All pixel messages remain bounded and validated.

HDR tone mapping/rotation runs at native resolution in a dedicated compute
shader before readback. The worker explicitly identifies already-converted SDR
pixels so the parent cannot apply HDR conversion twice. Ordinary BGRA rows copy
directly rather than decode/re-encode every SDR channel. The shader is compared
against the CPU reference for all rotations, SDR/HDR, three white levels and
negative/nonfinite channels, allowing at most one code value of rounding error.

Copy publishes complete CF_DIB and sRGB PNG data with movable native allocations;
no delayed callback or OLE conversion depends on the editor remaining alive.
The clipboard PNG uses fast lossless compression; normal saved files retain WIC
compression. Both formats contain only the flattened selection. Hosted UI tests
read bitmap/PNG/KVM data after destroying the clipboard-owner window. They write
a synthetic clipboard on the CI desktop, never on the user's active desktop.
`--screenshot-latency-test` measures synthetic 2560×1440 preparation without OS
clipboard writes. `--screenshot-warm-capture-test` measures real cold/prewarmed
capture without windows, input or saved desktop images. These timings exclude
physical display/input latency and vary with GPU/CPU load.
