#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.1'
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
    $setup = Join-Path $dist "VoxType-$Version-win-x64-Setup.exe"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    foreach ($artifact in @($zip, $setup, (Join-Path $dist 'SHA256SUMS.txt'), (Join-Path $dist 'build-info.json'))) {
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
    foreach ($file in @('VoxType.App.exe','VoxType.App.dll','VoxType.App.runtimeconfig.json','coreclr.dll','Microsoft.UI.Xaml.dll','Assets/AppIcon.ico','ThirdPartyNotices.md','scripts/start-r2t2.ps1','R2T2.Server.exe','R2T2.Server.runtimeconfig.json','Microsoft.AspNetCore.Server.Kestrel.Core.dll','runtimes/win-x64/whisper.dll','runtimes/vulkan/win-x64/whisper.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "Incomplete release: $file" }
    }
    if (Test-Path -LiteralPath (Join-Path $publish 'services/R2T2.Server/coreclr.dll')) { throw 'R2T2 runtime was duplicated instead of merged.' }
    $adapterRuntime = Get-Content -LiteralPath (Join-Path $publish 'R2T2.Server.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($adapterRuntime.runtimeOptions.PSObject.Properties.Name -contains 'frameworks' -or $adapterRuntime.runtimeOptions.PSObject.Properties.Name -contains 'framework') {
        throw 'R2T2 adapter unexpectedly requires a shared .NET runtime.'
    }
    $runtime = Get-Content -LiteralPath (Join-Path $publish 'VoxType.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.PSObject.Properties.Name -contains 'frameworks' -or $runtime.runtimeOptions.PSObject.Properties.Name -contains 'framework') {
        throw 'Portable application unexpectedly requires a shared .NET runtime.'
    }
    $instructions = (Get-Content -LiteralPath (Join-Path $root 'docs/portable-readme.txt') -Raw).Replace('@VERSION@', $Version)
    Set-Content -LiteralPath (Join-Path $publish 'README.txt') -Value $instructions -Encoding utf8
    $commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source commit.' }
    $dirty = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source status.' }
    $info = [ordered]@{ version=$Version; sourceCommit="$commit"; sourceDirty=($dirty.Count -gt 0); testsTotal=[int]$counters.total; testsPassed=[int]$counters.passed; builtUtc=[DateTime]::UtcNow.ToString('o') }
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'build-info.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $dist 'build-info.json') -Destination (Join-Path $publish 'build-info.json')
    & $iscc /Qp "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'installer/VoxType.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    @($setup, $zip, (Join-Path $dist 'build-info.json')) | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
    } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release artifacts: $dist"
    Write-Host "Publish directory: $publish"
    Write-Host "Test report: $reports/release.trx"
} finally { Pop-Location }
