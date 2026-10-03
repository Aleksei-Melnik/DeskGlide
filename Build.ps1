param([string]$Output=(Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference='Stop'
dotnet publish (Join-Path $PSScriptRoot 'src/ScreenCapture.csproj') -c Release -r win-x64 --self-contained true -o $Output
if($LASTEXITCODE -ne 0){throw 'Build failed. Exit the app before replacing an existing installation.'}
# Same apphost, bound to ScreenCapture.dll. Retains old shortcuts/firewall rules.
Copy-Item -LiteralPath (Join-Path $Output 'ScreenCapture.exe') -Destination (Join-Path $Output 'SdrCapture.exe') -Force

