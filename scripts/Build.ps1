param([switch]$Publish,[string]$OutputDirectory,[switch]$NoRestore)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$root=Split-Path -Parent $PSScriptRoot
[xml]$project=Get-Content (Join-Path $root 'src/Companion.Diagnostics/Companion.Diagnostics.csproj') -Raw
$version=$project.Project.PropertyGroup.Version
$package=if($OutputDirectory){[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $root $OutputDirectory}))}else{Join-Path $root ('artifacts/QATK-'+$version+'-win-x64-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
if($Publish -and (Test-Path -LiteralPath $package) -and (!(Test-Path -LiteralPath $package -PathType Container) -or @(Get-ChildItem -LiteralPath $package -Force).Count -gt 0)){throw '发布目录必须为空；不会覆盖运行中的助手、旧包或恢复备份。请指定新的 -OutputDirectory。'}
[string[]]$restoreArgs=@();if($NoRestore){$restoreArgs=@('--no-restore')}
Push-Location $root
try {
    New-Item -ItemType Directory -Force evidence | Out-Null
    if ($Publish) {
        & dotnet publish src/Companion.Diagnostics -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' @restoreArgs -o $package 2>&1 | Tee-Object evidence/publish-qatk.txt
    } else {
        & dotnet build src/Companion.Diagnostics -c Release @restoreArgs 2>&1 | Tee-Object evidence/build.txt
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet 失败，退出码 $LASTEXITCODE；见 evidence 日志。" }
    if ($Publish) {
        if(@(Get-ChildItem -LiteralPath $package -File -Recurse|Where-Object Name -ne 'QATK.exe').Count -gt 0){throw '单文件构建产生了意外旁文件，停止打包。'}
        Copy-Item docs/PLAYER-README.md (Join-Path $package 'README.md')
        Copy-Item THIRD_PARTY_NOTICES.md -Destination $package
        $projectLicense=if(Test-Path LICENSE){'LICENSE'}else{'../LICENSE'}
        Copy-Item $projectLicense (Join-Path $package 'PROJECT-LICENSE.txt')
        foreach ($id in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
            foreach ($version in Get-ChildItem (Join-Path .packages $id) -Directory) {
                $licenses=Join-Path $package "licenses/$id-$($version.Name)"
                New-Item -ItemType Directory -Force $licenses | Out-Null
                Get-ChildItem $version.FullName -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' | Copy-Item -Destination $licenses
            }
        }
        if (Test-Path licenses) { Copy-Item licenses/* (Join-Path $package 'licenses') -Recurse -Force }
        if(@(Get-ChildItem $package -Recurse -File -Filter '*.exe').Count -ne 1){throw '玩家包必须只有一个 EXE 入口。'}
        Get-ChildItem $package -File -Recurse | Where-Object Name -ne 'SHA256.json' |
            Get-FileHash -Algorithm SHA256 | Select-Object @{n='Path';e={[IO.Path]::GetRelativePath($package,$_.Path).Replace('\','/')}},@{n='Sha256';e={$_.Hash}} |
            ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $package 'SHA256.json')
        Write-Output "本地候选目录：$package（未上传或发布）"
    }
} finally { Pop-Location }
