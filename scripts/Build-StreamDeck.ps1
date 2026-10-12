param([string]$Output=(Join-Path $PSScriptRoot '../artifacts/streamdeck'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$Output=[IO.Path]::GetFullPath($Output)
Push-Location (Join-Path $repo 'streamdeck')
try{
    pnpm install --frozen-lockfile
    if($LASTEXITCODE -ne 0){throw 'Stream Deck dependency install failed.'}
    pnpm run build
    if($LASTEXITCODE -ne 0){throw 'Stream Deck build failed.'}
    & ./scripts/icons.ps1
    pnpm run test
    if($LASTEXITCODE -ne 0){throw 'Stream Deck tests failed.'}
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    & ./node_modules/.bin/streamdeck.cmd pack ./com.yami.deskglide.sdPlugin --output $Output --force
    if($LASTEXITCODE -ne 0){throw 'Official Stream Deck validation/packaging failed.'}
    Write-Output (Join-Path $Output 'com.yami.deskglide.streamDeckPlugin')
}finally{Pop-Location}
