# Recording validation — DeskGlide 0.7.31

Windows 11, RTX 5070, native 2560 × 1440 HDR display. The installed recorder
was closed for these tests. Test executables and recordings use isolated,
ignored directories; profiles, recordings, audio references and signing keys
are excluded from source and release packages.

## Remaining repeat/drop defect

A 60-second AV1/native1440p120 desktop/NDI trace had seven repeated output
images and zero repeated source pixel IDs. Replaying its source timestamps
through the old selector reproduced all seven repetitions. The 3/4-period
acceptance window sometimes rejected an available image slightly behind its
lower bound, advanced to the next image early and then repeated that image.
Allowing a full output period on either side eliminated those repetitions in
the same trace. The existing 100 ms playout allowance supplies this lookahead;
output timestamps, audio timing and real source gaps are preserved.

A regression uses the measured relative source times -8.689, 0, 12.438 and
20.425 ms with the observed recording phase. It reproduces one repeat with
the old window and consumes the available images chronologically without a
repeat after the correction. Independent jitter/phase tests at 60/120 FPS
remain required.

## Capture and conversion

Counting repeated IDs is insufficient to establish uniform motion. The
144-to-120 acquisition/output ratio also produced alternating short/long
steps in a uniformly moving 240 Hz source. A deterministic clock-domain test
reproduces this beat at both 60 and 120 FPS. Integer acquisition rates
(120/240 Hz respectively) eliminate the beat in that test. Physical capture
and GPU overhead measurements for this candidate are pending below.

Pacing now derives deadlines from the frame index, retaining fractional QPC
ticks instead of accumulating a truncated interval. Tests cover exact
one-hour endpoints at 60/120/240 Hz and intervals differing by at most one tick.

NV12 preparation now retains an immutable source reference and runs outside
the capture worker. A blocked-conversion test verifies that capture can offer
new images without waiting. Queue depth and bytes are bounded independently
(eight images, 64 MiB, allowing one image when it exceeds that byte budget).
Overload drops the oldest pending reference and increments a counter.
Source timestamps and recording generation survive queueing; clear and
shutdown release pending references. The production recorder rejects a
conversion completed for an obsolete generation.

AVX2 packing processes 16 pixels at once. Random pixel tests check every
luma/chroma value, vector boundaries and scalar tails; separate processes
also run with AVX2 disabled and with all hardware intrinsics disabled.
The HDR-to-SDR transform and limited-range BT.709 values are unchanged.

## Desktop acquisition

The D3D11 source presents at 240 Hz and carries a frame ID and its complement
at both top and bottom. Decoded markers must match throughout each export.
The fullscreen fixture covers animated desktop backgrounds, which can
otherwise publish desktop updates without changing the window being measured.
Present-call timestamps and DXGI statistics are diagnostic and do not alone
prove actual scanout timing.

| Export after correction | Frames | Repeated IDs | Invalid markers | Recording queue drops |
| --- | ---: | ---: | ---: | ---: |
| AV1 native 1440p120 + NDI | 6,600 | 0 | 0 | 0 |
| HEVC scaled 4K120 + NDI | 6,600 | 0 | 0 | 0 |

Further H.264, encoder/scaling and long audio/save-boundary checks are still
running. This document must be completed before publishing the release.

## Limits

These tests establish the behavior of the measured pipeline, not universal
NVIDIA App equivalence. A real presentation/capture stall can still require
CFR output to hold its last image. The previous windowed-fixture repetitions
cannot all be attributed to the encoder; several already existed in raw
captured pixels. No interpolation conceals source gaps. No Windows security,
display, HDR or game settings are changed to obtain these results.
