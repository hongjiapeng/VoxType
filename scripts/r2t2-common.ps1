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

function Expand-R2T2Runtime {
    param([string]$Archive, [string]$Destination)
    $runtimeRoot = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $repairs = [Collections.Generic.List[object]]::new()
        $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            if (-not $entry.Name) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $entry.FullName))
            if (-not $target.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase) -or -not $targets.Add($target)) {
                throw '运行时压缩包包含无效或重复的文件路径。'
            }
            $entryStream = $entry.Open()
            try { $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)) }
            finally { $entryStream.Dispose() }
            try {
                if ((Test-Path -LiteralPath $target -PathType Leaf) -and
                    (Get-Item -LiteralPath $target).Length -eq $entry.Length -and
                    (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $expectedHash) { continue }
            } catch {
                throw "无法校验运行时文件：$target。文件可能被独占使用或没有读取权限，请先在 R2T2 服务窗口按 Ctrl+C 后重试。已有文件保留。"
            }
            $repairs.Add([pscustomobject]@{ Entry = $entry; Target = $target })
        }
        if (-not $targets.Contains((Join-Path $runtimeRoot 'llama-server.exe'))) { throw '运行时压缩包中没有 llama-server.exe。' }
        # Check every replacement before modifying anything. Mapped DLLs cannot be overwritten.
        foreach ($repair in $repairs) {
            if (-not (Test-Path -LiteralPath $repair.Target)) { continue }
            try {
                $probe = [IO.File]::Open($repair.Target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
            } catch {
                throw "运行时文件正在使用或不可写：$($repair.Target)。请先在 R2T2 服务窗口按 Ctrl+C；若旧窗口已关闭，请在任务管理器结束该目录的 llama-server 后重试。已有文件保留。"
            }
        }
        foreach ($repair in $repairs) {
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($repair.Target))
            $temporary = "$($repair.Target).$([Guid]::NewGuid().ToString('N')).partial"
            try {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($repair.Entry, $temporary, $false)
                [IO.File]::Move($temporary, $repair.Target, $true)
            } finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            }
        }
        if ($repairs.Count -eq 0) { Write-Host '运行时文件已验证，复用现有文件。' }
        else { Write-Host "运行时准备完成，已恢复 $($repairs.Count) 个文件。" }
    } finally { $zip.Dispose() }
}

function Assert-R2T2PortAvailable {
    param([Net.IPAddress]$Address, [int]$Port)
    $probe = [Net.Sockets.TcpListener]::new($Address, $Port)
    try { $probe.Start() }
    catch {
        $socketError = $_.Exception.GetBaseException()
        if ($socketError -is [Net.Sockets.SocketException] -and $socketError.SocketErrorCode -eq [Net.Sockets.SocketError]::AddressAlreadyInUse) {
            $owners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
                $ownerProcess = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
                $ownerName = if ($null -ne $ownerProcess) { $ownerProcess.ProcessName } else { '已退出' }
                "PID $($_.OwningProcess) ($ownerName)"
            } | Sort-Object -Unique)
            $ownerText = if ($owners.Count) { '，占用进程：' + ($owners -join '、') } else { '' }
            throw "端口 $Port 已被占用$ownerText。若为旧 R2T2 服务，请先在旧窗口按 Ctrl+C；旧窗口已关闭时，在任务管理器结束上述旧服务进程后重试。也可指定其他 -Port 和 -LlamaPort。"
        }
        throw "无法监听 ${Address}:$Port，套接字错误：$($socketError.Message)"
    }
    finally { $probe.Stop() }
}
