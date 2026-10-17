#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('HuggingFace', 'HfMirror')][string]$Source = 'HuggingFace',
    [ValidateSet('Cpu', 'Vulkan')][string]$Backend = 'Cpu',
    [string]$DataDirectory = (Join-Path $PSScriptRoot '../r2t2-data'),
    [switch]$ModelsOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'r2t2-common.ps1')
$manifest = Get-R2T2Manifest
$dataRoot = [IO.Path]::GetFullPath($DataDirectory)
$modelDirectory = Join-Path $dataRoot 'models'
$null = New-Item -ItemType Directory -Path $modelDirectory -Force
$downloadHost = if ($Source -eq 'HfMirror') { 'https://hf-mirror.com' } else { 'https://huggingface.co' }
if ($Source -eq 'HfMirror') { Write-Host '已选择 HF-Mirror 第三方镜像；仍使用官方模型哈希校验。' }

function Save-VerifiedAsset {
    param([string]$Url, [string]$Destination, $Asset, [switch]$Gguf)
    if (Test-R2T2Asset $Destination $Asset -Gguf:$Gguf) {
        Write-Host "已验证：$($Asset.name)"
        return
    }
    if (Test-Path -LiteralPath $Destination) { throw "现有文件校验失败，请手动移走后重试：$Destination" }
    $temporary = "$Destination.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        Write-Host "下载 $($Asset.name) ($([Math]::Round($Asset.size / 1GB, 2)) GiB)…"
        # Explicit source only: a failed download never silently switches hosts.
        Invoke-WebRequest -Uri $Url -OutFile $temporary -TimeoutSec 1800
        if (-not (Test-R2T2Asset $temporary $Asset -Gguf:$Gguf)) { throw "大小、格式或 SHA-256 校验失败：$($Asset.name)" }
        [IO.File]::Move($temporary, $Destination, $false)
        Write-Host "校验完成：$($Asset.name)"
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

foreach ($model in $manifest.models) {
    $url = "$downloadHost/$($manifest.modelRepository)/resolve/$($manifest.modelRevision)/$($model.name)"
    Save-VerifiedAsset $url (Join-Path $modelDirectory $model.name) $model -Gguf
}
if (-not $ModelsOnly) {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw '运行时自动下载仅支持 Windows x64；其他设备使用 Docker 或自己安装 llama-server。模型已准备完成。' }
    $runtime = $manifest.runtimes.$Backend
    $runtimeDirectory = Join-Path $dataRoot "runtime/$Backend"
    $null = New-Item -ItemType Directory -Path $runtimeDirectory -Force
    $archive = Join-Path $runtimeDirectory $runtime.name
    $url = "https://github.com/ggml-org/llama.cpp/releases/download/$($manifest.runtimeVersion)/$($runtime.name)"
    Save-VerifiedAsset $url $archive $runtime
    # Verify extracted files against the pinned archive; leave identical loaded DLLs alone.
    Expand-R2T2Runtime -Archive $archive -Destination $runtimeDirectory
}
Write-Host "准备完成：$dataRoot"
Write-Host '模型权重遵循网易有道 MODEL_LICENSE，详见 docs/r2t2-server.md。'
