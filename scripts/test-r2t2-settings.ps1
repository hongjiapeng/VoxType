#Requires -Version 7.0
# Run against an isolated Debug --test-profile configured for r2t2-local/cpu.
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [ValidateSet('local', 'sandbox')][string]$Target = 'local'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
function Checked([string[]]$Arguments) {
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments)
    if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    $output
}
function UI([string[]]$Arguments) {
    $selector = if ($script:hwnd) { @('-w', $script:hwnd) } else { @('-a', "$AppPid") }
    Checked (@('ui') + $Arguments + @('--on', $Target) + $selector)
}
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body | Out-Null; $results.Add(@{ name = $Name; status = 'PASS' }) }
    catch { $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" }) }
}
function Screenshot([string]$Name) {
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Evidence already exists: $path" }
    Checked @('ui', 'screenshot', '--output', $path, '--on', $Target, '-w', $hwnd) | Out-Null
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Screenshot missing: $path" }
    $screenshots.Add($path)
}
function SelectProvider([string]$Name) {
    UI @('invoke', 'ProviderSelector', '--action', 'expand') | Out-Null
    $tree = UI @('inspect', 'ProviderSelector', '--json') | ConvertFrom-Json
    $items = @($tree.windows.elements.children | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Name })
    $selectors = @($items.selector | Select-Object -Unique)
    if ($selectors.Count -ne 1) { throw "Expected one provider item: $Name" }
    UI @('invoke', $selectors[0], '--action', 'select') | Out-Null
    UI @('invoke', 'ProviderSelector', '--action', 'collapse') | Out-Null
}
try {
    UI @('wait-for', 'NavRecognition', '-t', '10000') | Out-Null
    $windows = @(UI @('list-windows', '--json') | ConvertFrom-Json)
    $main = @($windows | Where-Object title -eq 'VoxType 设置')
    if ($main.Count -ne 1) { throw 'Expected exactly one settings window.' }
    $hwnd = [string]$main[0].hwnd
    Test 'R2T2 controls and initial state' {
        UI @('invoke', 'NavRecognition')
        UI @('wait-for', 'R2T2InferenceBackend', '--value', 'cpu', '-t', '3000')
        UI @('wait-for', 'StopLocalR2T2', '-p', 'IsEnabled', '--value', 'False')
        UI @('scroll-into-view', 'R2T2Status')
        Screenshot '01-r2t2-idle.png'
    }
    Test 'Invalid directory shows actionable inline error' {
        UI @('set-value', 'R2T2DataDirectory', 'relative-path')
        UI @('invoke', 'PrepareLocalR2T2')
        UI @('wait-for', 'R2T2Status', '--value', '完整路径', '--contains', '-t', '3000')
        UI @('wait-for', 'PrepareLocalR2T2', '-p', 'IsEnabled', '--value', 'True')
        Screenshot '02-r2t2-error.png'
    }
    Test 'Background launch waits for health and locks resource controls' {
        UI @('set-value', 'R2T2DataDirectory', $DataDirectory)
        UI @('invoke', 'LaunchLocalR2T2')
        UI @('wait-for', 'R2T2Status', '--value', '已就绪', '--contains', '-t', '20000')
        UI @('wait-for', 'PrepareLocalR2T2', '-p', 'IsEnabled', '--value', 'False')
        UI @('wait-for', 'LaunchLocalR2T2', '-p', 'IsEnabled', '--value', 'False')
        UI @('wait-for', 'R2T2InferenceBackend', '-p', 'IsEnabled', '--value', 'False')
        UI @('wait-for', 'R2T2DataDirectory', '-p', 'IsEnabled', '--value', 'False')
        UI @('wait-for', 'StopLocalR2T2', '-p', 'IsEnabled', '--value', 'True')
        Screenshot '03-r2t2-ready.png'
        if ($Target -eq 'local') {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$AppPid")
            if (@($children | Where-Object Name -in @('pwsh.exe', 'powershell.exe', 'dotnet.exe', 'R2T2.Server.exe')).Count) { throw 'Application launched a shell, SDK or HTTP adapter process.' }
            if (@($children | Where-Object Name -eq 'llama-server.exe').Count -ne 1) { throw 'Expected exactly one inference process.' }
        }
    }
    Test 'Stop releases controls and allows immediate restart' {
        UI @('invoke', 'StopLocalR2T2')
        UI @('wait-for', 'R2T2Status', '--value', '已停止', '--contains', '-t', '5000')
        UI @('wait-for', 'R2T2DataDirectory', '-p', 'IsEnabled', '--value', 'True')
        UI @('invoke', 'LaunchLocalR2T2')
        UI @('wait-for', 'R2T2Status', '--value', '已就绪', '--contains', '-t', '20000')
        UI @('invoke', 'StopLocalR2T2')
        UI @('wait-for', 'R2T2Status', '--value', '已停止', '--contains', '-t', '5000')
        UI @('wait-for', 'StopLocalR2T2', '-p', 'IsEnabled', '--value', 'False')
        Screenshot '04-r2t2-stopped.png'
    }
    Test 'Local and server modes expose distinct controls and preserve the server URL' {
        UI @('scroll-into-view', 'ProviderSelector')
        SelectProvider 'R2T2 服务器'
        UI @('wait-for', 'ProviderSelector', '--value', 'R2T2 服务器', '-t', '3000')
        UI @('wait-for', 'R2T2InferenceBackend', '--gone', '-t', '3000')
        UI @('wait-for', 'BaseUrl', '--value', 'http://127.0.0.1:8272/v1', '-t', '3000')
        UI @('scroll-into-view', 'BaseUrl')
        Screenshot '05-r2t2-server.png'
        UI @('scroll-into-view', 'ProviderSelector')
        SelectProvider 'R2T2 本机'
        UI @('wait-for', 'ProviderSelector', '--value', 'R2T2 本机', '-t', '3000')
        UI @('wait-for', 'BaseUrl', '--gone', '-t', '3000')
        UI @('wait-for', 'R2T2InferenceBackend', '--value', 'cpu', '-t', '3000')
    }
} catch { $results.Add(@{ name = 'UI setup'; status = 'FAIL'; detail = "$_" }) }
finally {
    try { Checked @('ui', 'yield', '--on', $Target) | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
}
$failed = @($results | Where-Object status -eq 'FAIL').Count
$report = [ordered]@{ target = $Target; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed; results = @($results.ToArray()); screenshots = @($screenshots.ToArray()) }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'test-results.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
if ($failed) { exit 1 }
