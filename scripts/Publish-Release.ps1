param([Parameter(Mandatory)][string]$ReleaseFolder,[Parameter(Mandatory)][string]$NotesFile,[string]$Repository='Aleksei-Melnik/DeskGlide',[string]$TargetCommit='')
$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath (Join-Path $ReleaseFolder 'update.json') -Raw | ConvertFrom-Json
if(!$TargetCommit){$TargetCommit=(git rev-parse HEAD).Trim()}
if($TargetCommit -notmatch '^[a-f0-9]{40}$'){throw 'Expected an exact release commit'}
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/Assets/release-public.pem')))
    if(!$rsa.VerifyData([IO.File]::ReadAllBytes((Join-Path $ReleaseFolder 'update.json')),[IO.File]::ReadAllBytes((Join-Path $ReleaseFolder 'update.sig')),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)){throw 'Release signature is invalid'}
}finally{$rsa.Dispose()}
foreach($artifact in @($manifest,$manifest.Portable)){
    $file=Get-Item -LiteralPath (Join-Path $ReleaseFolder $artifact.File)
    if($file.Length -ne $artifact.Size -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $artifact.Sha256){throw 'Release artifact integrity check failed'}
}
$token=$env:GITHUB_TOKEN
if(!$token){
    $credential=@('protocol=https','host=github.com','','') | git credential fill
    foreach($line in $credential){if($line.StartsWith('password=')){$token=$line.Substring(9)}}
}
if(!$token){throw 'Sign in through Git Credential Manager or set GITHUB_TOKEN'}
$headers=@{Authorization="Bearer $token";'User-Agent'='DeskGlide-publisher';Accept='application/vnd.github+json'}
$base="https://api.github.com/repos/$Repository"
$body=@{tag_name="v$($manifest.Version)";target_commitish=$TargetCommit;name="DeskGlide $($manifest.Version)";body=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile));draft=$true;prerelease=$false} | ConvertTo-Json
$release=Invoke-RestMethod "$base/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
foreach($name in @($manifest.Portable.File,"DeskGlide-$($manifest.Version)-win-x64.zip",$manifest.File,'update.json','update.sig','com.yami.deskglide.streamDeckPlugin')){
    $url=$release.upload_url.Split('{')[0]+'?name='+[Uri]::EscapeDataString($name)
    $asset=Invoke-RestMethod $url -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile (Join-Path $ReleaseFolder $name)
    if($asset.state -ne 'uploaded'){throw "Upload incomplete: $name. Release left as draft."}
}
$published=Invoke-RestMethod "$base/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}'
Write-Output $published.html_url

