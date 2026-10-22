#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.3'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $iscc) {
        $iscc = @(
            "${env:LOCALAPPDATA}/Programs/Inno Setup 6/ISCC.exe",
            "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe",
            "${env:ProgramFiles}/Inno Setup 6/ISCC.exe"
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $iscc) { throw 'Install Inno Setup 6 before building the installer.' }
    # Fresh directories prevent stale files from entering a new release.
    $run = "$Version-$([guid]::NewGuid().ToString('N'))"
    $publish = Join-Path $root "artifacts/publish/$run"
    $reports = Join-Path $root "artifacts/test-results/$run"
    $dist = Join-Path $root "dist/$Version"
    $zip = Join-Path $dist "VoxType-$Version-win-x64-Portable.zip"
    $serverZip = Join-Path $dist "VoxType-R2T2-Server-$Version-win-x64.zip"
    $serverPublish = Join-Path $root "artifacts/server-publish/$run"
    $setup = Join-Path $dist "VoxType-$Version-win-x64-Setup.exe"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    foreach ($artifact in @($zip, $setup, $serverZip, (Join-Path $dist 'SHA256SUMS.txt'), (Join-Path $dist 'build-info.json'))) {
        if (Test-Path -LiteralPath $artifact) { throw "Release output already exists: $artifact. Choose a new version or move the existing artifacts." }
    }

    & dotnet test tests/VoxType.Tests/VoxType.Tests.csproj -c Release -p:Platform=x64 --logger 'trx;LogFileName=release.trx' --results-directory $reports
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $reports 'release.trx')
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -eq 0 -or [int]$counters.total -ne [int]$counters.passed) {
        throw 'Release test report does not show a fully passing execution.'
    }

    & dotnet publish src/VoxType.App/VoxType.App.csproj -c Release -p:Platform=x64 -p:PublishProfile=Portable "-p:Version=$Version" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    foreach ($file in @('VoxType.App.exe','VoxType.App.dll','VoxType.App.runtimeconfig.json','coreclr.dll','Microsoft.UI.Xaml.dll','Assets/AppIcon.ico','ThirdPartyNotices.md','VoxType.R2T2.Core.dll','services/r2t2-chat-template.jinja','runtimes/win-x64/whisper.dll','runtimes/vulkan/win-x64/whisper.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "Incomplete release: $file" }
    }
    if (@(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -like 'Microsoft.AspNetCore*' -or $_.Name -like 'R2T2.Server*' }).Count) { throw 'Server host dependencies leaked into the desktop package.' }
    $runtime = Get-Content -LiteralPath (Join-Path $publish 'VoxType.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.PSObject.Properties.Name -contains 'frameworks' -or $runtime.runtimeOptions.PSObject.Properties.Name -contains 'framework') {
        throw 'Portable application unexpectedly requires a shared .NET runtime.'
    }
    & dotnet publish services/R2T2.Server/R2T2.Server.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 "-p:Version=$Version" -p:PublishReadyToRun=false -p:DebugType=none -p:DebugSymbols=false -o $serverPublish
    if ($LASTEXITCODE -ne 0) { throw 'Standalone R2T2 server publish failed.' }
    foreach ($file in @('R2T2.Server.exe','R2T2.Server.runtimeconfig.json','VoxType.R2T2.Core.dll','coreclr.dll','Microsoft.AspNetCore.Server.Kestrel.Core.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $serverPublish $file) -PathType Leaf)) { throw "Incomplete standalone server: $file" }
    }
    $serverRuntime = Get-Content -LiteralPath (Join-Path $serverPublish 'R2T2.Server.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($serverRuntime.runtimeOptions.PSObject.Properties.Name -contains 'frameworks' -or $serverRuntime.runtimeOptions.PSObject.Properties.Name -contains 'framework') { throw 'Server unexpectedly requires a shared .NET runtime.' }
    New-Item -ItemType Directory -Force -Path (Join-Path $serverPublish 'scripts'),(Join-Path $serverPublish 'services'),(Join-Path $serverPublish 'docs'),(Join-Path $serverPublish 'licenses') | Out-Null
    foreach ($file in @('prepare-r2t2.ps1','start-r2t2.ps1','r2t2-common.ps1','r2t2-assets.json')) { Copy-Item -LiteralPath (Join-Path $root "scripts/$file") -Destination (Join-Path $serverPublish 'scripts') }
    Copy-Item -LiteralPath (Join-Path $root 'services/r2t2-chat-template.jinja') -Destination (Join-Path $serverPublish 'services')
    Copy-Item -LiteralPath (Join-Path $root 'docs/r2t2-server.md') -Destination (Join-Path $serverPublish 'docs')
    Copy-Item -LiteralPath (Join-Path $root 'docs/third-party-notices.md') -Destination (Join-Path $serverPublish 'ThirdPartyNotices.md')
    Copy-Item -Path (Join-Path $root 'docs/licenses/*') -Destination (Join-Path $serverPublish 'licenses')
    (Get-Content -LiteralPath (Join-Path $root 'docs/r2t2-server-readme.txt') -Raw).Replace('@VERSION@',$Version) | Set-Content -LiteralPath (Join-Path $serverPublish 'README.txt') -Encoding utf8
    $instructions = (Get-Content -LiteralPath (Join-Path $root 'docs/portable-readme.txt') -Raw).Replace('@VERSION@', $Version)
    Set-Content -LiteralPath (Join-Path $publish 'README.txt') -Value $instructions -Encoding utf8
    $commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source commit.' }
    $dirty = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source status.' }
    $info = [ordered]@{ version=$Version; sourceCommit="$commit"; sourceDirty=($dirty.Count -gt 0); testsTotal=[int]$counters.total; testsPassed=[int]$counters.passed; builtUtc=[DateTime]::UtcNow.ToString('o') }
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'build-info.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $dist 'build-info.json') -Destination (Join-Path $publish 'build-info.json')
    Copy-Item -LiteralPath (Join-Path $dist 'build-info.json') -Destination (Join-Path $serverPublish 'build-info.json')
    & $iscc /Qp "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'installer/VoxType.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $serverPublish '*') -DestinationPath $serverZip -CompressionLevel Optimal
    @($setup, $zip, $serverZip, (Join-Path $dist 'build-info.json')) | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release artifacts: $dist"
    Write-Host "Publish directory: $publish"
    Write-Host "Test report: $reports/release.trx"
} finally { Pop-Location }
