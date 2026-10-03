# Requires PowerShell 7. Never put the signing key inside this repository.
param([Parameter(Mandatory)][string]$SigningKey)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
[xml]$project=Get-Content -LiteralPath (Join-Path $repo 'src/ScreenCapture.csproj')
$version=[string]$project.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Expected numeric version x.y.z'}
$keyPath=(Resolve-Path -LiteralPath $SigningKey).Path
if($keyPath.StartsWith($repo+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Keep the private key outside the repository'}
$rsa=[Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem([IO.File]::ReadAllText($keyPath))
$public=[Security.Cryptography.RSA]::Create()
$public.ImportFromPem([IO.File]::ReadAllText((Join-Path $repo 'src/Assets/release-public.pem')))
if([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToBase64String($public.ExportSubjectPublicKeyInfo())){throw 'Signing key does not match the public key embedded in the app'}
$release=Join-Path $repo ('dist/'+$version+'-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $release 'payload'
& (Join-Path $repo 'Build.ps1') -Output $payload
foreach($file in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','Install-RecordingTools.ps1','Enable-Lan.ps1')){Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $payload}
# Retain license texts supplied by each managed dependency package.
$licenses=Join-Path $payload 'dependency-licenses';New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'licenses') -File | Copy-Item -Destination $licenses
$assets=Get-Content -LiteralPath (Join-Path $repo 'src/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
foreach($lib in $assets.libraries.GetEnumerator()){
    if($lib.Value.type -ne 'package'){continue}
    foreach($base in $assets.packageFolders.Keys){
        $package=Join-Path $base $lib.Value.path
        if(!(Test-Path -LiteralPath $package)){continue}
        foreach($license in Get-ChildItem -LiteralPath $package -File | Where-Object Name -Match '^(license|copying|third-party-notices)' ){
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenses ($lib.Key.Replace('/','-')+'-'+$license.Name))
        }
    }
}
# Only a fresh publish directory is packaged; no profiles, keys, tests or clips.
$files=[ordered]@{}
foreach($item in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName){
    $relative=[IO.Path]::GetRelativePath($payload,$item.FullName).Replace('\','/')
    $files[$relative]=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
}
$name="ScreenCapture-$version-win-x64.zip";$zip=Join-Path $release $name
[IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$manifest=[ordered]@{Version=$version;File=$name;Size=(Get-Item -LiteralPath $zip).Length;Sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash;Files=$files}
$json=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8))
[IO.File]::WriteAllBytes((Join-Path $release 'update.json'),$json)
[IO.File]::WriteAllBytes((Join-Path $release 'update.sig'),$rsa.SignData($json,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))
$rsa.Dispose();$public.Dispose()
Write-Output $release

