# Third-party components

ScreenCapture source is MIT licensed. The following components keep their own
copyright notices and licenses:

- .NET runtime and Windows Desktop runtime: Microsoft and contributors, MIT.
  https://github.com/dotnet/runtime and https://github.com/dotnet/winforms
  Runtime packages include their license and third-party notice files.
- Vortice.Windows / Vortice.Mathematics: Amer Koleci and contributors, MIT.
  https://github.com/amerkoleci/Vortice.Windows
- SharpGen.Runtime: Alexandre Mutel, Jeremy Koritzinsky, Amer Koleci and contributors, MIT.
  https://github.com/SharpGenTools/SharpGenTools
- NAudio: Mark Heath and contributors, MIT. https://github.com/naudio/NAudio

Dependency license texts from the restored NuGet packages are included in the
release's dependency-licenses directory when available.

NDI is a trademark of Vizrt NDI AB. ScreenCapture is an independent project,
not an official NDI application. The NDI runtime is installed separately under
its vendor terms: https://ndi.video/ and https://docs.ndi.video/

FFmpeg is a separate optional executable, downloaded directly by the user from
https://www.gyan.dev/ffmpeg/builds/ . The pinned Gyan build is GPLv3; its license
is retained in tools/FFmpeg-LICENSE and its corresponding source/build details
are linked from the upstream build page. FFmpeg binaries are not bundled in
this project's GitHub release. See https://ffmpeg.org/legal.html .

