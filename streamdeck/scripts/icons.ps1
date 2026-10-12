$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Join-Path $PSScriptRoot '../com.yami.deskglide.sdPlugin/imgs'
New-Item -ItemType Directory -Path $root -Force | Out-Null
# Reuse the existing DeskGlide vector monogram generator without rewriting app assets.
$source=Get-Content (Join-Path $PSScriptRoot '../../src/Assets/Build-Icon.ps1') -Raw
$functions=$source.Substring(0,$source.IndexOf('$sizes ='))
Invoke-Expression $functions
foreach($size in @(256,512)){
    $bitmap=New-IconBitmap $size
    try{$name=if($size -eq 256){'plugin.png'}else{'plugin@2x.png'};$bitmap.Save((Join-Path $root $name),[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
}
