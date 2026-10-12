param([string]$Output=(Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference='Stop'
$plugin=Join-Path $PSScriptRoot 'artifacts/streamdeck/com.yami.deskglide.streamDeckPlugin'
if(!(Test-Path -LiteralPath $plugin)){throw 'Build the Stream Deck plugin first: ./scripts/Build-StreamDeck.ps1'}
New-Item -ItemType Directory -Path (Join-Path $Output 'streamdeck') -Force | Out-Null
Copy-Item -LiteralPath $plugin -Destination (Join-Path $Output 'streamdeck/com.yami.deskglide.streamDeckPlugin') -Force
& (Join-Path $PSScriptRoot 'scripts/Build-Camera.ps1') -Output (Join-Path $Output 'camera')
dotnet publish (Join-Path $PSScriptRoot 'src/ScreenCapture.csproj') -c Release -r win-x64 --self-contained true -o $Output
if($LASTEXITCODE -ne 0){throw 'Build failed. Exit the app before replacing an existing installation.'}
# Public launcher and legacy aliases share the apphost and singleton.
Copy-Item -LiteralPath (Join-Path $Output 'ScreenCapture.exe') -Destination (Join-Path $Output 'DeskGlide.exe') -Force
Copy-Item -LiteralPath (Join-Path $Output 'ScreenCapture.exe') -Destination (Join-Path $Output 'SdrCapture.exe') -Force

