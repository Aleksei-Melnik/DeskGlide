DeskGlide 0.7.17

- Improved replay stability under CPU/GPU pressure. Video uses a direct NV12 path with 25% fewer pipe bytes, bounded frame history and dedicated video/audio writers that wake without waiting for normal-priority thread-pool callbacks.
- Improved recording audio continuity: larger WASAPI capture buffers and a shared, delayed recording clock give complete packets time to arrive. NDI transmission keeps its existing timing.
- A successful Save starts a new unsaved interval. The next clip contains only new footage since the previous Save, up to the chosen duration. Failed exports retain unsaved footage; saving does not restart the encoder. Updates, shutdown and settings changes still do not export clips automatically.
- Fixed frame boundaries when assembling clips with AAC audio. Export keeps decoding preroll without shifting the visible video or skipping frames between consecutive saves.
- The quality dropdown now explains Ultra, High, Medium and Low and shows the NVIDIA bitrate ceiling for the selected resolution and FPS. Labels update with the settings and support English/Russian. AMD/Intel use quality targets; their bitrate is not a fixed ceiling.

For 1440p120, start with **AV1 + High** on hardware that supports it, or HEVC + High. High's NVIDIA ceiling is 80 Mbps; Ultra is 120 Mbps. For 1080p60, High's ceiling is 23 Mbps. H.264 remains available for broader player/editor compatibility. The [quality guide](https://github.com/Aleksei-Melnik/DeskGlide#instant-replay) explains every level.

Validation: NVIDIA AV1/HEVC/H.264 and AMD H.264/HEVC passed decoded recording checks at 60 and 120 FPS under artificial CPU/GPU pressure, including audio continuity and consecutive saves. A long HDR AV1 1440p120 test passed without losing available source frames or accumulating A/V drift. The exact portable EXE and GitHub's interface/build tests passed too. [Results and limits](https://github.com/Aleksei-Melnik/DeskGlide/blob/main/HARDWARE-VALIDATION.md): these checks do not establish zero game-FPS overhead, every GPU generation, physical Intel support or 4K/8K performance.

Update through **Updates → Update all PCs**, or download **DeskGlide.exe**. Existing profiles, pairing, camera setup and recording tools are preserved. No files need to be copied beside the portable executable.
