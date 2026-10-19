#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Cpu', 'Vulkan')][string]$Backend = 'Cpu',
    [string]$DataDirectory = (Join-Path $PSScriptRoot '../r2t2-data'),
    [string]$LlamaServerPath = '',
    [string]$ListenAddress = '127.0.0.1',
    [ValidateRange(1024, 65535)][int]$Port = 8272,
    [ValidateRange(1024, 65535)][int]$LlamaPort = 8273,
    [ValidateRange(1, 256)][int]$Threads = [Math]::Min(8, [Environment]::ProcessorCount),
    [ValidateRange(1, 1800)][int]$StartupTimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'r2t2-common.ps1')
if (-not $IsWindows) { throw '此启动脚本用于 Windows，Linux 使用 Docker Compose。' }
$ip = $null
if (-not [Net.IPAddress]::TryParse($ListenAddress, [ref]$ip)) { throw 'ListenAddress 必须是 IP 地址。' }
$apiKey = [Environment]::GetEnvironmentVariable('R2T2_API_KEY')
if (-not [Net.IPAddress]::IsLoopback($ip) -and [string]::IsNullOrWhiteSpace($apiKey)) { throw '局域网监听需要先设置环境变量 R2T2_API_KEY。' }
if ($Port -eq $LlamaPort) { throw '适配服务和 llama-server 必须使用不同端口。' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dataRoot = [IO.Path]::GetFullPath($DataDirectory)
$manifest = Get-R2T2Manifest
foreach ($model in $manifest.models) {
    if (-not (Test-R2T2Asset (Join-Path $dataRoot "models/$($model.name)") $model -Gguf)) { throw '模型未准备好或校验失败，请先执行 scripts/prepare-r2t2.ps1。' }
}
if (-not $LlamaServerPath) { $LlamaServerPath = Join-Path $dataRoot "runtime/$Backend/llama-server.exe" }
$LlamaServerPath = [IO.Path]::GetFullPath($LlamaServerPath)
if (-not (Test-Path -LiteralPath $LlamaServerPath -PathType Leaf)) { throw "找不到 llama-server：$LlamaServerPath" }
foreach ($binding in @(@{ Address = $ip; Port = $Port }, @{ Address = [Net.IPAddress]::Loopback; Port = $LlamaPort })) {
    Assert-R2T2PortAvailable -Address $binding.Address -Port $binding.Port
}
$adapterExecutable = Join-Path $repoRoot 'R2T2.Server.exe'
$adapterArguments = @()
if (-not (Test-Path -LiteralPath $adapterExecutable -PathType Leaf)) {
    # Source checkout only. Installed releases ship a self-contained executable.
    $project = Join-Path $repoRoot 'services/R2T2.Server/R2T2.Server.csproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw '安装缺少 R2T2 服务，请重新安装完整的 VoxType 安装包。' }
    $publishDirectory = Join-Path $dataRoot 'adapter'
    $buildDirectory = Join-Path $dataRoot 'build'
    $intermediateDirectory = (Join-Path $buildDirectory 'obj') + [IO.Path]::DirectorySeparatorChar
    $outputDirectory = (Join-Path $buildDirectory 'bin') + [IO.Path]::DirectorySeparatorChar
    & dotnet publish $project -c Release -p:Platform=x64 --no-self-contained -o $publishDirectory `
        "-p:BaseIntermediateOutputPath=$intermediateDirectory" "-p:MSBuildProjectExtensionsPath=$intermediateDirectory" "-p:BaseOutputPath=$outputDirectory" --nologo
    if ($LASTEXITCODE -ne 0) { throw '适配服务构建失败。' }
    $adapterExecutable = 'dotnet'
    $adapterArguments = @((Join-Path $publishDirectory 'R2T2.Server.dll'))
}
$llamaArguments = @('-m', (Join-Path $dataRoot "models/$($manifest.models[0].name)"), '--mmproj', (Join-Path $dataRoot "models/$($manifest.models[1].name)"),
    '--host', '127.0.0.1', '--port', "$LlamaPort", '--alias', 'r2t2-q4', '-c', '8192', '-np', '1', '-t', "$Threads", '--temp', '0',
    '--jinja', '--chat-template-file', (Join-Path $repoRoot 'services/r2t2-chat-template.jinja'), '--log-disable')
if ($Backend -eq 'Cpu') { $llamaArguments += @('-ngl', '0', '--device', 'none', '--no-mmproj-offload') }
else { $llamaArguments += @('-ngl', '99') }

function Start-OwnedProcess {
    param([string]$Executable, [string[]]$Arguments, [hashtable]$EnvironmentValues = @{})
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = $repoRoot
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in $EnvironmentValues.Keys) { $info.Environment[$name] = $EnvironmentValues[$name] }
    # Remove inherited llama configuration: only the explicit flags above configure this child.
    foreach ($name in @($info.Environment.Keys)) { if ($name.StartsWith('LLAMA_ARG_')) { $null = $info.Environment.Remove($name) } }
    return [Diagnostics.Process]::Start($info)
}

$llama = $null
$adapter = $null
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(2)
try {
    $llama = Start-OwnedProcess $LlamaServerPath $llamaArguments
    $hostLiteral = if ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) { "[$ip]" } else { "$ip" }
    $adapter = Start-OwnedProcess $adapterExecutable $adapterArguments @{
        'R2T2__ListenUrl' = "http://${hostLiteral}:$Port"; 'R2T2__UpstreamUrl' = "http://127.0.0.1:$LlamaPort"
        'R2T2__ApiKey' = [string]$apiKey; 'R2T2__UpstreamApiKey' = ''; 'R2T2__Model' = 'r2t2-q4'; 'R2T2__TimeoutSeconds' = '115'
        'Logging__LogLevel__Default' = 'Warning'; 'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'
    }
    $probeAddress = if ($ip.Equals([Net.IPAddress]::Any)) { '127.0.0.1' } elseif ($ip.Equals([Net.IPAddress]::IPv6Any)) { '[::1]' } else { $hostLiteral }
    $healthUrl = "http://${probeAddress}:$Port/health"
    Write-Host '正在加载 Q4 模型…'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    while ($timer.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
        if ($llama.HasExited -or $adapter.HasExited) { throw '子进程已退出，请检查运行时、模型和端口配置。' }
        try {
            $response = $client.GetAsync($healthUrl).GetAwaiter().GetResult()
            try { $ready = $response.IsSuccessStatusCode } finally { $response.Dispose() }
        } catch [Net.Http.HttpRequestException] { } catch [Threading.Tasks.TaskCanceledException] { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw '模型加载超时，可增大 StartupTimeoutSeconds 或检查运行时。' }
    Write-Host "服务就绪。VoxType Base URL：http://${hostLiteral}:$Port/v1；模型：r2t2-q4。Ctrl+C 停止两个子进程。"
    while (-not $llama.HasExited -and -not $adapter.HasExited) { Start-Sleep -Seconds 1 }
    throw '识别服务子进程已退出。'
} finally {
    $client.Dispose()
    foreach ($child in @($adapter, $llama)) {
        if ($null -ne $child) {
            try { if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() } }
            finally { $child.Dispose() }
        }
    }
}
