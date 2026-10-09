# Third-party components

DeskGlide source is MIT licensed. The following components keep their own
copyright notices and licenses:

- .NET runtime and Windows Desktop runtime: Microsoft and contributors, MIT.
  https://github.com/dotnet/runtime and https://github.com/dotnet/winforms
  Runtime packages include their license and third-party notice files.
- Vortice.Windows / Vortice.Mathematics: Amer Koleci and contributors, MIT.
  https://github.com/amerkoleci/Vortice.Windows
- SharpGen.Runtime: Alexandre Mutel, Jeremy Koritzinsky, Amer Koleci and contributors, MIT.
  https://github.com/SharpGenTools/SharpGenTools
- NAudio: Mark Heath and contributors, MIT. https://github.com/naudio/NAudio
- DirectShow base classes: Microsoft, MIT, Windows-classic-samples commit
  434f6002bdf9cf9829406c3ff2b33387982d6168. The source, upstream reference and
  license are retained in native/Camera/baseclasses; the license is also in releases.
  https://github.com/microsoft/Windows-classic-samples

Optional Discord audio uses the original, signed VB-CABLE installer, downloaded
on request from VB-Audio. VB-CABLE is donationware by VB-Audio / Vincent Burel,
not a DeskGlide driver. It is never installed automatically or on a KVM host.
Users can identify, license or support the author through the settings page:
https://www.vb-cable.com/ and https://vb-audio.com/Services/licensing.htm .
The original package is SHA-256 pinned and its installer signature is verified.

Dependency license texts from the restored NuGet packages are included in the
release's dependency-licenses directory when available.

NDI is a trademark of Vizrt NDI AB. DeskGlide is an independent project,
not an official NDI application. When needed, DeskGlide downloads the unmodified
NDI Runtime installer directly from downloads.ndi.tv, verifies its pinned hash
and Windows signature, and opens the original vendor license wizard. Runtime
binaries are not included in the app release. Installation remains subject to
the vendor terms: https://ndi.video/ and https://docs.ndi.video/

FFmpeg is a separate optional executable, downloaded directly by the user from
https://www.gyan.dev/ffmpeg/builds/ . The pinned Gyan build is GPLv3; its license
is retained in tools/FFmpeg-LICENSE and its corresponding source/build details
are linked from the upstream build page. FFmpeg binaries are not bundled in
this project's GitHub release. See https://ffmpeg.org/legal.html .

KVM JPEG encoding/decoding uses the unmodified x64 libjpeg-turbo 3.2.0 binary
from its official distribution. This software is based in part on the work of
the Independent JPEG Group. IJG and BSD license texts are retained in
licenses/libjpeg-turbo, with binary provenance in native/TurboJpeg/UPSTREAM.md.
https://github.com/libjpeg-turbo/libjpeg-turbo

