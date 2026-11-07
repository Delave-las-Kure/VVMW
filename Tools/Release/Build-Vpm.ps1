#requires -Version 7.2
# Build a reproducible VPM archive from the committed package tree.
# Sparse checkout assets may be read from a verified installed copy; every blob is checked.
[CmdletBinding()]
param(
    [string]$AssetSource='D:/unityhub/worlds/Uran Fracture Cinema/Packages/idv.jlchntoz.vvmw',
    [string]$OutputPath='D:/unityhub/worlds/.ufc-releases/vizvid/1.8.2-ufc.1'
)
$ErrorActionPreference='Stop'
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$releasePackage='Packages/idv.jlchntoz.vvmw'
$releaseManifest=Get-Content -LiteralPath (Join-Path $releaseRoot "$releasePackage/package.json") -Raw | ConvertFrom-Json
if($releaseManifest.name -ne 'idv.jlchntoz.vvmw' -or $releaseManifest.version -ne '1.8.2-ufc.1'){throw 'Unrecognized release identity'}
function Read-Git([string[]]$Arguments){
    $value=& git -c "safe.directory=$releaseRoot" -C $releaseRoot @Arguments
    if($LASTEXITCODE -ne 0){throw ('Git failed: '+($Arguments -join ' '))};return $value
}
function Get-BlobHash([byte[]]$Bytes){
    $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try{$hash.AppendData([Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)`0"));$hash.AppendData($Bytes);return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()}
    finally{$hash.Dispose()}
}
$releaseCommit=Read-Git -Arguments @('rev-parse','HEAD')
$releaseChanges=@(Read-Git -Arguments @('diff','--name-only','1.8.2','HEAD','--',$releasePackage))
if(($releaseChanges -join '|') -ne "$releasePackage/Runtime/VVMW/Core_Timing.cs|$releasePackage/package.json"){throw 'Release contains unexpected package changes'}
$releaseFiles=Read-Git -Arguments @('-c','core.quotepath=false','ls-tree','-r','HEAD','--',$releasePackage)
$releasePayload=Join-Path $OutputPath 'package'
if(Test-Path -LiteralPath $OutputPath){throw 'Output already exists; choose a new directory to retain previous artifacts'}
$null=[IO.Directory]::CreateDirectory($releasePayload)
$releaseVerified=0
foreach($record in $releaseFiles){
    if($record -notmatch '^100644 blob ([a-f0-9]{40})\t(.+)$'){throw "Unsupported package record: $record"}
    $expected=$Matches[1];$repoPath=$Matches[2];$relative=$repoPath.Substring($releasePackage.Length+1)
    $found=$false
    foreach($candidate in @((Join-Path $releaseRoot $repoPath),(Join-Path $AssetSource $relative))){
        if(-not (Test-Path -LiteralPath $candidate -PathType Leaf)){continue}
        $bytes=[IO.File]::ReadAllBytes($candidate)
        if((Get-BlobHash $bytes) -ne $expected){continue}
        $target=Join-Path $releasePayload $relative
        $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target));[IO.File]::WriteAllBytes($target,$bytes)
        $releaseVerified++;$found=$true;break
    }
    if(-not $found){throw "No exact committed blob for $relative ($expected)"}
}
$releaseZip=Join-Path $OutputPath 'idv.jlchntoz.vvmw-1.8.2-ufc.1.zip'
$zip=[IO.Compression.ZipFile]::Open($releaseZip,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in (Get-ChildItem -LiteralPath $releasePayload -File -Recurse | Sort-Object FullName -CaseSensitive)){
        $entryName=[IO.Path]::GetRelativePath($releasePayload,$file.FullName).Replace('\','/')
        $entry=$zip.CreateEntry($entryName,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $output=$entry.Open();try{$bytes=[IO.File]::ReadAllBytes($file.FullName);$output.Write($bytes,0,$bytes.Length)}finally{$output.Dispose()}
    }
}finally{$zip.Dispose()}
$releaseZipHash=(Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant()
$releaseManifest | Add-Member -NotePropertyName url -NotePropertyValue 'https://github.com/Delave-las-Kure/VVMW/releases/download/ufc-v1.8.2-1/idv.jlchntoz.vvmw-1.8.2-ufc.1.zip'
$releaseManifest | Add-Member -NotePropertyName zipSHA256 -NotePropertyValue $releaseZipHash
$listing=[ordered]@{name='UFC VizVid Stable';id='world.ufc.vvmw';url='https://raw.githubusercontent.com/Delave-las-Kure/VVMW/ufc/cinema-stable/VPM/index.json';author='Delave-las-Kure';packages=@{'idv.jlchntoz.vvmw'=@{versions=@{'1.8.2-ufc.1'=$releaseManifest}}}}
$utf8=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $OutputPath 'index.json'),($listing | ConvertTo-Json -Depth 20)+"`n",$utf8)
$report=[ordered]@{pass=$true;version=$releaseManifest.version;sourceCommit=$releaseCommit;upstream='0ddb33be520b7a11e614d1d5d13c358d9cf0ec42';packageTree=(Read-Git -Arguments @('rev-parse',"HEAD:$releasePackage"));verifiedPackageFiles=$releaseVerified;packageChanges=$releaseChanges;zipSHA256=$releaseZipHash;archive=$releaseZip;mirrorsIncluded=$false}
[IO.File]::WriteAllText((Join-Path $OutputPath 'release.json'),($report | ConvertTo-Json -Depth 10)+"`n",$utf8)
$report | ConvertTo-Json -Depth 10
