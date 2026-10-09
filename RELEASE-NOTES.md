DeskGlide 0.7.16

- Fixed NDI Runtime setup retry when its cached installer is damaged or incomplete. The original installer is downloaded again and verified before replacement; invalid or oversized cache files are not reused.
- Includes automatic NDI Runtime setup and NVIDIA NVENC / AMD AMF / Intel Quick Sync replay selection from 0.7.15. An existing Runtime is reused. First installation still requires the vendor's license wizard and Windows permission prompt. No drivers or dependencies are placed beside the portable EXE.
- Added an explicit audio-device option for the live diagnostic, so testing an active endpoint does not alter the user's recording profile.

Hardware validation: native 1440p replay passed decoded moving-frame checks with no repeats or skips for NVIDIA H.264/HEVC at 60 FPS, HEVC/AV1 at 120 FPS, and AMD H.264 at 60 FPS and HEVC at 60/120 FPS. Mixed, three-track and silent audio modes passed. A 180-second NVIDIA HDR desktop recording completed without an encoder restart; local NDI delivery ran for 90 seconds without receive timeouts. Real RODECaster Duo audio met the recording deadline with zero late samples. Local KVM video reached 30/60 FPS, and replay still exports only after an explicit Save.

These measurements cover the tested RTX 5070 and integrated Radeon. They do not establish physical Intel support, every GPU generation, sustained game-load performance, remote LAN performance, interactive first-time NDI installation or 4K/8K throughput. Full details: [hardware validation](https://github.com/Aleksei-Melnik/DeskGlide/blob/main/HARDWARE-VALIDATION.md).

Update through Updates → Update all PCs, or download DeskGlide.exe. Existing profiles, pairing, virtual camera setup and recording tools are preserved.
