param([string]$AppFolder=$PSScriptRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath (Join-Path $AppFolder 'app/ScreenCapture.exe')){$AppFolder=Join-Path $AppFolder 'app'}
$destination=Join-Path $AppFolder 'tools'
if(Test-Path -LiteralPath (Join-Path $destination 'ffmpeg.exe')){Write-Host 'FFmpeg already installed; keeping the existing version.';return}
$work=Join-Path ([IO.Path]::GetTempPath()) ('ScreenCapture-ffmpeg-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$zip=Join-Path $work 'ffmpeg.zip'
Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip' -OutFile $zip
$expected='60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected){throw 'FFmpeg archive hash mismatch; nothing installed.'}
Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'unpacked')
$root=Join-Path $work 'unpacked/ffmpeg-9.0.2-essentials_build'
foreach($name in @('ffmpeg.exe','ffprobe.exe')){if(!(Test-Path -LiteralPath (Join-Path $root "bin/$name"))){throw "Missing $name"}}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($name in @('ffmpeg.exe','ffprobe.exe')){Copy-Item -LiteralPath (Join-Path $root "bin/$name") -Destination (Join-Path $destination $name)}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $destination 'FFmpeg-LICENSE')
Write-Host "Recording tools installed in $destination. Download cache: $work"

