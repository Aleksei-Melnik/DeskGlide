param([string]$Output=(Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'scripts/Build-Camera.ps1') -Output (Join-Path $Output 'camera')
dotnet publish (Join-Path $PSScriptRoot 'src/ScreenCapture.csproj') -c Release -r win-x64 --self-contained true -o $Output
if($LASTEXITCODE -ne 0){throw 'Build failed. Exit the app before replacing an existing installation.'}
# Public launcher and legacy aliases share the apphost and singleton.
Copy-Item -LiteralPath (Join-Path $Output 'ScreenCapture.exe') -Destination (Join-Path $Output 'DeskGlide.exe') -Force
Copy-Item -LiteralPath (Join-Path $Output 'ScreenCapture.exe') -Destination (Join-Path $Output 'SdrCapture.exe') -Force

