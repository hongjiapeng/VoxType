#Requires -Version 7.0
# Two self-contained apphosts can share an output directory when shared files are identical.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$PublishDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$destination = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($source.Equals($destination, [StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase) -or $destination.StartsWith($source, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish source and destination must be separate directories.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'R2T2.Server.exe') -PathType Leaf)) { throw 'Compiled adapter is missing.' }
$copies = [Collections.Generic.List[object]]::new()
$reused = 0
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $target = Join-Path $destination $relative
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or $file.Length -ne (Get-Item -LiteralPath $target).Length -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
            throw "Conflicting app/service publish file: $relative. Align runtime/package versions before publishing."
        }
        $reused++
    } else { $copies.Add([pscustomobject]@{ Source = $file.FullName; Target = $target }) }
}
# Check all collisions first: an incompatible service must not partly overwrite the app.
foreach ($copy in $copies) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy.Target))
    Copy-Item -LiteralPath $copy.Source -Destination $copy.Target
}
Write-Host "R2T2 publish merged; reused $reused identical files, added $($copies.Count) service files."
