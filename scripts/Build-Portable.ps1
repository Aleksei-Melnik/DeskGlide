param([string]$Payload=(Join-Path $PSScriptRoot '../app'),[string]$Output=(Join-Path $PSScriptRoot '../portable'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$Payload=[IO.Path]::GetFullPath($Payload)
$Output=[IO.Path]::GetFullPath($Output)
$camera=Join-Path $Payload 'camera/ScreenCapture.Camera.dll'
if(!(Test-Path -LiteralPath $camera)){throw 'Build the folder payload first (Build.ps1).'}
$resourceRoot=Join-Path $repo ('src/obj/portable-resources-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resourceRoot -Force | Out-Null
$archivePath=Join-Path $resourceRoot 'resources.zip'
$archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
$entryNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Resource([string]$file,[string]$name){if($entryNames.Add($name)){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file,$name) | Out-Null}}
try{
    Add-Resource $camera 'camera/ScreenCapture.Camera.dll'
    foreach($name in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','Enable-Lan.ps1')){
        Add-Resource (Join-Path $repo $name) $name
    }
    foreach($folder in @('licenses','dependency-licenses')){
        $base=Join-Path $Payload $folder
        if(Test-Path -LiteralPath $base){foreach($file in Get-ChildItem -LiteralPath $base -File -Recurse){
            $relative=$folder+'/'+[IO.Path]::GetRelativePath($base,$file.FullName).Replace('\','/')
            Add-Resource $file.FullName $relative
        }}
    }
    foreach($file in Get-ChildItem -LiteralPath $Payload -File | Where-Object Name -Match '^(license|thirdparty|third-party|copying)'){
        Add-Resource $file.FullName $file.Name
    }
    # Also cover direct developer builds, before New-Release adds package notices.
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo 'licenses') -File){
        $name='dependency-licenses/'+$file.Name
        Add-Resource $file.FullName $name
    }
    $name='dependency-licenses/Microsoft-DirectShow-baseclasses-LICENSE'
    Add-Resource (Join-Path $repo 'native/Camera/baseclasses/LICENSE') $name
}finally{$archive.Dispose()}
dotnet publish (Join-Path $repo 'src/ScreenCapture.csproj') -c Release -r win-x64 --self-contained true -o $Output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded "-p:PortableResourcesArchive=$archivePath"
if($LASTEXITCODE -ne 0){throw 'Portable build failed.'}
if(Get-ChildItem -LiteralPath $Output -File | Where-Object Extension -EQ '.dll'){throw 'A required library was left outside the executable.'}
Move-Item -LiteralPath (Join-Path $Output 'ScreenCapture.exe') -Destination (Join-Path $Output 'DeskGlide.exe') -Force
Write-Output (Join-Path $Output 'DeskGlide.exe')
