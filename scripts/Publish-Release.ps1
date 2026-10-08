param([Parameter(Mandatory)][string]$ReleaseFolder,[Parameter(Mandatory)][string]$NotesFile,[string]$Repository='Aleksei-Melnik/DeskGlide')
$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath (Join-Path $ReleaseFolder 'update.json') -Raw | ConvertFrom-Json
$token=$env:GITHUB_TOKEN
if(!$token){
    $credential=@('protocol=https','host=github.com','','') | git credential fill
    foreach($line in $credential){if($line.StartsWith('password=')){$token=$line.Substring(9)}}
}
if(!$token){throw 'Sign in through Git Credential Manager or set GITHUB_TOKEN'}
$headers=@{Authorization="Bearer $token";'User-Agent'='DeskGlide-publisher';Accept='application/vnd.github+json'}
$base="https://api.github.com/repos/$Repository"
$body=@{tag_name="v$($manifest.Version)";target_commitish='main';name="DeskGlide $($manifest.Version)";body=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile));draft=$true;prerelease=$false} | ConvertTo-Json
$release=Invoke-RestMethod "$base/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
foreach($name in @("DeskGlide-$($manifest.Version)-win-x64.zip",$manifest.File,'update.json','update.sig')){
    $url=$release.upload_url.Split('{')[0]+'?name='+[Uri]::EscapeDataString($name)
    $asset=Invoke-RestMethod $url -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile (Join-Path $ReleaseFolder $name)
    if($asset.state -ne 'uploaded'){throw "Upload incomplete: $name. Release left as draft."}
}
$published=Invoke-RestMethod "$base/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}'
Write-Output $published.html_url

