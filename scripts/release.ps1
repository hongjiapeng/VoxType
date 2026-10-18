#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)][ValidatePattern('^v?\d+\.\d+\.\d+$')][string]$Version,
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$versionNumber = $Version.TrimStart('v')
$tag = "v$versionNumber"
$repo = 'hongjiapeng/VoxType'
$root = Split-Path $PSScriptRoot -Parent
function Checked([string]$File, [string[]]$Arguments) {
    $output = @(& $File @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $File $($Arguments -join ' ')" }
    $output
}
Push-Location $root
try {
    if (@(Checked git @('status','--porcelain')).Count) { throw 'Commit all intended changes before publishing.' }
    if ((Checked git @('branch','--show-current')) -ne 'master') { throw 'Publish from master after merging the intended changes.' }
    $remote = Checked git @('remote','get-url','origin')
    if ($remote -notmatch '^https://github\.com/hongjiapeng/VoxType(?:\.git)?/?$|^git@github\.com:hongjiapeng/VoxType(?:\.git)?$') { throw 'Origin is not the VoxType repository.' }
    [xml]$props = Get-Content Directory.Build.props
    if ($props.Project.PropertyGroup.Version -ne $versionNumber) { throw 'Update the source Version to match the release.' }
    $commit = Checked git @('rev-parse','HEAD')
    if (@(Checked git @('tag','--list',$tag)).Count -or @(Checked git @('ls-remote','--tags','origin',"refs/tags/$tag")).Count) { throw "Tag $tag already exists." }
    Checked gh @('repo','view',$repo,'--json','nameWithOwner') | Out-Null
    $notes = Join-Path $root "docs/releases/$tag.md"
    if (-not (Test-Path -LiteralPath $notes)) { throw "Release notes missing: $notes" }
    $dist = Join-Path $root "dist/$versionNumber"
    if ($DryRun) {
        Write-Host "Would build/verify $versionNumber from $commit, push master and annotated $tag, upload a draft with four assets, verify uploads, then publish a prerelease."
        return
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'SHA256SUMS.txt'))) {
        & (Join-Path $PSScriptRoot 'build-release.ps1') -Version $versionNumber
    }
    $info = Get-Content -LiteralPath (Join-Path $dist 'build-info.json') -Raw | ConvertFrom-Json
    if ($info.sourceCommit -ne $commit -or $info.sourceDirty -or $info.version -ne $versionNumber -or $info.testsTotal -le 0 -or $info.testsPassed -ne $info.testsTotal) {
        throw 'Artifacts must come from this clean commit with a fully passing Release test report.'
    }
    $names = @("VoxType-$versionNumber-win-x64-Setup.exe", "VoxType-$versionNumber-win-x64-Portable.zip", 'build-info.json')
    $lines = @(Get-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt'))
    if ($lines.Count -ne $names.Count) { throw 'Checksum manifest is incomplete.' }
    foreach ($name in $names) {
        $file = Join-Path $dist $name
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($lines -cnotcontains "$actual  $name") { throw "Checksum mismatch: $name" }
    }
    if (@(Checked git @('status','--porcelain')).Count) { throw 'Build changed source files; review and commit them before releasing.' }
    Checked git @('push','origin','master') | Out-Host
    Checked git @('tag','-a',$tag,'-m',"VoxType $tag preview") | Out-Host
    Checked git @('push','origin',$tag) | Out-Host
    $assets = @($names + 'SHA256SUMS.txt') | ForEach-Object { Join-Path $dist $_ }
    Checked gh (@('release','create',$tag,'--repo',$repo,'--verify-tag','--draft','--prerelease','--title',"VoxType $tag",'--notes-file',$notes) + $assets) | Out-Host
    $release = (Checked gh @('release','view',$tag,'--repo',$repo,'--json','assets,isDraft,url')) -join "`n" | ConvertFrom-Json
    foreach ($file in $assets) {
        $asset = @($release.assets | Where-Object name -eq ([IO.Path]::GetFileName($file)))
        if ($asset.Count -ne 1 -or $asset[0].size -ne (Get-Item -LiteralPath $file).Length) { throw 'Draft upload verification failed; draft retained.' }
    }
    Checked gh @('release','edit',$tag,'--repo',$repo,'--draft=false','--prerelease') | Out-Host
    Checked gh @('release','view',$tag,'--repo',$repo,'--json','url,isDraft,isPrerelease,assets') | Out-Host
} finally { Pop-Location }
