param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ZipPath,
    [ValidateSet('github','gitee')][string]$Channel='github'
)
# Local preparation only: no git, upload, credentials, network or player replacement.
$ErrorActionPreference='Stop'
if($Version -notmatch '^v?\d+\.\d+(?:\.\d+){0,2}$'){throw 'Invalid release version'}
$tag='v'+$Version.TrimStart('v')
$parts=$Version.TrimStart('v').Split('.')
$normalized=[version](($parts+@('0')*(4-$parts.Count))-join '.')
$exe=Get-Item -LiteralPath $ExePath
if($exe.Name -ne 'QATK.exe' -or $exe.Length -lt 1 -or $exe.Length -gt 512MB){throw 'Invalid player executable'}
$actual=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName)
if([version]::new($actual.FileMajorPart,$actual.FileMinorPart,$actual.FileBuildPart,$actual.FilePrivatePart) -ne $normalized){throw 'Release version does not match executable version'}
$notes=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesPath))
if([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 32000){throw 'Update notes required, maximum 32000 characters'}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Output already exists; refusing to overwrite'}
$sha=(Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
$feed=@{draft=$false;prerelease=$false;tag_name=$tag;body=$notes;assets=@(@{name='QATK.exe';size=$exe.Length;digest='sha256:'+$sha.ToLowerInvariant();browser_download_url="https://github.com/sangshiCHI/QATracker/releases/download/$tag/QATK.exe"})}
if($ZipPath){
    $zip=Get-Item -LiteralPath $ZipPath
    if($zip.Extension -ne '.zip' -or $zip.Length -lt 1 -or $zip.Length -gt 100MB){throw 'Portable ZIP must fit the free domestic attachment limit'}
    Add-Type -AssemblyName System.IO.Compression
    $archive=[IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try{
        $players=@($archive.Entries|Where-Object {$_.FullName -ieq 'QATK.exe'})
        if($players.Count -ne 1 -or $players[0].FullName -cne 'QATK.exe' -or $players[0].Length -ne $exe.Length){throw 'ZIP must contain the exact player at its root'}
        $stream=$players[0].Open()
        try{$inner=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($inner -ne $sha){throw 'ZIP player does not match source executable'}
    }finally{$archive.Dispose()}
    $base=if($Channel -eq 'gitee'){'https://gitee.com/sangshichi/QATracker'}else{'https://github.com/sangshiCHI/QATracker'}
    $feed.assets=@(@{name='QATK.zip';size=$zip.Length;digest='sha256:'+(Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant();browser_download_url="$base/releases/download/$tag/QATK.zip";executable_digest='sha256:'+$sha.ToLowerInvariant();executable_size=$exe.Length})
}elseif($Channel -eq 'gitee'){throw 'Domestic releases require a verified portable ZIP'}
[void][IO.Directory]::CreateDirectory($out)
[IO.File]::WriteAllText((Join-Path $out 'latest.json'),($feed|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
@{LocalPreparationOnly=$true;Published=$false;Version=$tag;ExeSha256=$sha;ExeSize=$exe.Length;RequiredFutureRepoPath='companion/releases/latest.json';SourceExe=$exe.FullName}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'preparation.json')
Write-Output "Prepared local metadata: $out (not uploaded or published)"
