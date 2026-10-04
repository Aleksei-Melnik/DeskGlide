param([Parameter(Mandatory)][string]$Output,[switch]$Probe)
$ErrorActionPreference='Stop'
$Output=[IO.Path]::GetFullPath($Output)
$source=Join-Path $PSScriptRoot '../native/Camera'
$objects=Join-Path $source 'obj'
New-Item -ItemType Directory -Path $objects,$Output -Force | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){$installation=& $vswhere -products '*' -property installationPath | Where-Object {Test-Path (Join-Path $_ 'SDK/ScopeCppSDK/vc15/VC/bin/cl.exe')} | Select-Object -First 1}
if(!$installation){throw 'Install Visual Studio C++ desktop build tools.'}
$scope=Join-Path $installation 'SDK/ScopeCppSDK/vc15'
if(Test-Path (Join-Path $scope 'VC/bin/cl.exe')){
    $compiler=Join-Path $scope 'VC/bin/cl.exe'
    $env:INCLUDE="$scope/VC/include;$scope/SDK/include/ucrt;$scope/SDK/include/shared;$scope/SDK/include/um"
    $env:LIB="$scope/VC/lib;$scope/SDK/lib"
}else{
    $dev=Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
    $command='""'+$dev+'" -no_logo -arch=x64 -host_arch=x64 >nul && set"'
    $environment=& cmd.exe /d /s /c $command
    foreach($line in $environment){if($line -match '^([^=]+)=(.*)$'){[Environment]::SetEnvironmentVariable($matches[1],$matches[2],'Process')}}
    $compiler=(Get-Command cl.exe).Source
}
$units=@('amfilter','amvideo','combase','dllentry','mtype','source','wxdebug','wxlist','wxutil','ctlutil','schedule','refclock','sysclock','winutil') | ForEach-Object {Join-Path $source "baseclasses/$_.cpp"}
$dll=Join-Path ([IO.Path]::GetFullPath($Output)) 'ScreenCapture.Camera.dll'
Push-Location $objects
try{
    & $compiler /nologo /LD /O2 /MT /EHsc /W3 /DUNICODE /D_UNICODE /DWIN32 /D_WIN32_WINNT=0x0602 /D_CRT_SECURE_NO_WARNINGS "/I$source/baseclasses" (Join-Path $source 'Camera.cpp') @units /link "/OUT:$dll" "/DEF:$source/Camera.def" strmiids.lib ole32.lib oleaut32.lib uuid.lib winmm.lib advapi32.lib user32.lib gdi32.lib
    if($LASTEXITCODE -ne 0){throw 'Camera native build failed'}
    if($Probe){& $compiler /nologo /O2 /MT /EHsc /W3 (Join-Path $source 'Probe.cpp') /link "/OUT:$(Join-Path ([IO.Path]::GetFullPath($Output)) 'CameraProbe.exe')" strmiids.lib ole32.lib oleaut32.lib uuid.lib advapi32.lib; if($LASTEXITCODE -ne 0){throw 'Camera probe build failed'}}
}finally{Pop-Location}
