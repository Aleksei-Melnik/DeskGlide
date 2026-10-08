param([string]$AppFolder=$PSScriptRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath (Join-Path $AppFolder 'app/ScreenCapture.exe')){$AppFolder=Join-Path $AppFolder 'app'}
foreach($name in @('DeskGlide','ScreenCapture','SdrCapture')){
    $captureExe=Join-Path $AppFolder "$name.exe"
    if(!(Test-Path -LiteralPath $captureExe)){continue}
    $ruleName="$name-NDI-Wired-LAN"
    $existing=Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if($existing){$existing | Remove-NetFirewallRule}
    New-NetFirewallRule -Name $ruleName -DisplayName "$name (wired private local subnet)" -Direction Inbound -Action Allow -Protocol Any -RemoteAddress LocalSubnet -InterfaceType Wired -Profile Private -Program $captureExe | Out-Null
}
Write-Host 'DeskGlide wired private local subnet access enabled.'

