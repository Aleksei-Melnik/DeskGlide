DeskGlide 0.7.15

- NDI Runtime setup starts automatically when an NDI feature first needs it. The original NDI 6.3.2 installer is downloaded into user data and checked by SHA-256 and Windows signature. Complete its license wizard and Windows permission prompt once; an existing Runtime is reused without reinstalling it. A cancelled setup can be retried in Screen streaming and does not reopen repeatedly. Local replay and KVM remain usable without NDI.
- Instant replay now selects NVIDIA NVENC, AMD AMF or Intel Quick Sync automatically. Before recording, a two-frame encoder preflight checks the requested codec, resolution, quality and frame rate. NVIDIA's existing encoding settings are preserved. Unsupported choices show a clear error and never silently switch to CPU encoding or another codec.
- Updated English/Russian setup messages and recording settings. The portable app stays a single EXE with no dependency files beside it.

Use Updates → Update all PCs, or download DeskGlide.exe. Existing profiles, pairing, virtual camera setup and recording tools are preserved.

Validation: Windows CI checks dependency reuse, concurrent setup, cancelled/manual retry, runtime paths after installation, encoder selection/fallback and cancellation, the downloaded NDI installer hash and Windows signature, and existing normal/portable UI, KVM, camera, audio and update tests. Tests do not launch the NDI installer. Sustained recording on physical AMD/Intel cards and interactive first-time NDI installation have not yet been verified. No UI/input or GPU-encoding tests run on the gaming PC.
