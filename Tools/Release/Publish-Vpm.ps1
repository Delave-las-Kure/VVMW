#requires -Version 7.2
# Publish only the reviewed stable branch/tag and its exact archive. Default is read-only.
[CmdletBinding()]
param([ValidateSet('Check','Publish')][string]$Action='Check',[Parameter(Mandatory)][string]$ReleasePath)
$ErrorActionPreference='Stop'
$publishRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$publishSafe=$publishRoot.Replace('\','/')
$publishRepo='Delave-las-Kure/VVMW';$publishTag='ufc-v1.8.2-1'
$publishReport=Get-Content -LiteralPath (Join-Path $ReleasePath 'release.json') -Raw | ConvertFrom-Json
$publishListing=Get-Content -LiteralPath (Join-Path $publishRoot 'VPM/index.json') -Raw | ConvertFrom-Json
if(-not $publishReport.pass -or $publishReport.version -ne '1.8.2-ufc.1' -or $publishReport.mirrorsIncluded -or $publishReport.verifiedPackageFiles -ne 582){throw 'Unverified release report'}
if((Get-FileHash -LiteralPath $publishReport.archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $publishReport.zipSHA256 -or $publishListing.packages.'idv.jlchntoz.vvmw'.versions.'1.8.2-ufc.1'.zipSHA256 -ne $publishReport.zipSHA256){throw 'Archive/listing hash mismatch'}
function Invoke-PublishGit([string[]]$Arguments){
    $value=& git -c "safe.directory=$publishSafe" -c credential.username=Delave-las-Kure -C $publishRoot @Arguments
    if($LASTEXITCODE -ne 0){throw ('Git failed: '+($Arguments -join ' '))};return $value
}
if((Invoke-PublishGit -Arguments @('branch','--show-current')) -ne 'ufc/cinema-stable'){throw 'Stable branch required'}
if(Invoke-PublishGit -Arguments @('status','--porcelain')){throw 'Clean publishing worktree required'}
if((Invoke-PublishGit -Arguments @('remote','get-url','origin')) -ne 'https://github.com/Delave-las-Kure/VVMW.git'){throw 'Unexpected origin'}
if((Invoke-PublishGit -Arguments @('rev-parse',"$publishTag^{}")) -ne $publishReport.sourceCommit){throw 'Release tag does not match the reviewed source'}
$env:GCM_INTERACTIVE='Never';$env:GIT_TERMINAL_PROMPT='0'
$credentialLines=@("protocol=https`nhost=github.com`nusername=Delave-las-Kure`n`n" | & git credential fill)
if($LASTEXITCODE -ne 0){throw 'Sign in through Git Credential Manager as Delave-las-Kure'}
$credential=@{};foreach($line in $credentialLines){$split=$line.IndexOf('=');if($split -gt 0){$credential[$line.Substring(0,$split)]=$line.Substring($split+1)}}
if(-not $credential.password){throw 'No GitHub credential available'}
$headers=@{'User-Agent'='UFC-VPM-Release';'Authorization'=('Bearer '+$credential.password);'Accept'='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
$credentialLines=$null;$credential=$null
$api="https://api.github.com/repos/$publishRepo"
$repo=Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 30
if(-not $repo.permissions.push -or $repo.private){throw 'Expected write access to the public VVMW fork'}
if($Action -eq 'Check'){
    [ordered]@{pass=$true;repository=$publishRepo;branch='ufc/cinema-stable';tag=$publishTag;sourceCommit=$publishReport.sourceCommit;archiveSHA256=$publishReport.zipSHA256;canPublish=$true} | ConvertTo-Json;return
}
$null=Invoke-PublishGit -Arguments @('push','--atomic','origin','refs/heads/ufc/cinema-stable','refs/tags/ufc-v1.8.2-1')
$release=$null
try{$release=Invoke-RestMethod -Uri "$api/releases/tags/$publishTag" -Headers $headers -TimeoutSec 30}catch{if([int]$_.Exception.Response.StatusCode -ne 404){throw}}
if(-not $release){
    $body=@{tag_name=$publishTag;target_commitish=$publishReport.sourceCommit;name='VizVid (UFC Stable) 1.8.2-ufc.1';draft=$true;prerelease=$false;make_latest='false';body="VizVid 1.8.2 with the paused playback clock correction. Mirror features remain on a separate branch.`n`nThe upstream package ID, asset GUIDs, MIT license and author credit are preserved. The playing timing expression is unchanged.`n`nVPM repository: $($publishListing.url)`n`nArchive SHA256: $($publishReport.zipSHA256)`n`nVerified: exact 582-file package tree and deterministic archive; C#/Udon compilation; isolated sender/receiver and protected ClientSim checks with seeded media boundaries. Real multi-client VRChat acceptance is separate."} | ConvertTo-Json
    $release=Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 30
}
$checksum=Join-Path $ReleasePath 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksum,($publishReport.zipSHA256+'  '+[IO.Path]::GetFileName($publishReport.archive)+"`n"),[Text.UTF8Encoding]::new($false))
foreach($file in @($publishReport.archive,$checksum)){
    $name=[IO.Path]::GetFileName($file);$fileHash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset=$release.assets | Where-Object name -EQ $name | Select-Object -First 1
    if(-not $asset){
        if(-not $release.draft){throw 'Published release is missing an expected asset; refuse to alter it'}
        $upload=$release.upload_url.Split('{')[0]+'?name='+[Uri]::EscapeDataString($name)
        $asset=Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType 'application/octet-stream' -InFile $file -TimeoutSec 120
    }
    if($asset.digest -and $asset.digest -ne "sha256:$fileHash"){throw 'GitHub asset digest differs'}
}
if($release.draft){$release=Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"false"}' -TimeoutSec 30}
$publicZip=Join-Path $ReleasePath 'verified-public.zip'
Invoke-WebRequest -Uri $publishListing.packages.'idv.jlchntoz.vvmw'.versions.'1.8.2-ufc.1'.url -OutFile $publicZip -TimeoutSec 120
if((Get-FileHash -LiteralPath $publicZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $publishReport.zipSHA256){throw 'Public archive verification failed'}
$result=[ordered]@{pass=$true;release=$release.html_url;repository=$publishRepo;branch='ufc/cinema-stable';tag=$publishTag;source=$publishListing.url;zipSHA256=$publishReport.zipSHA256;publicArchiveVerified=$true}
[IO.File]::WriteAllText((Join-Path $ReleasePath 'publication.json'),($result | ConvertTo-Json)+"`n",[Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json
