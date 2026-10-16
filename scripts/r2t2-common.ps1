Set-StrictMode -Version Latest

function Test-R2T2Asset {
    param([string]$Path, $Asset, [switch]$Gguf)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Asset.size) { return $false }
    if ($Gguf) {
        $stream = [IO.File]::OpenRead($Path)
        try {
            $header = [byte[]]::new(4)
            if ($stream.Read($header, 0, 4) -ne 4 -or [Text.Encoding]::ASCII.GetString($header) -ne 'GGUF') { return $false }
        } finally { $stream.Dispose() }
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Asset.sha256
}

function Get-R2T2Manifest {
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'r2t2-assets.json') -Raw | ConvertFrom-Json
}
