param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$AppUserModelId,
    [Parameter(Mandatory)][string]$ProfileId,
    [Parameter(Mandatory)][string]$ImportModelPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/model-manager-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
function Checked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    $output
}
function UI([string[]]$Arguments) {
    $selector = if ($script:mainHwnd -and $Arguments[0] -ne 'list-windows') { @('-w', "$script:mainHwnd") } else { @('-a', "$AppPid") }
    Checked (@('ui') + $Arguments + @('--on', 'local') + $selector)
}
function WindowUI([string]$Window, [string[]]$Arguments) { Checked (@('ui') + $Arguments + @('--on', 'local', '-w', $Window)) }
function Walk($Nodes) { foreach ($node in $Nodes) { $node; Walk $node.children } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body | Out-Null; $results.Add([pscustomobject]@{ name=$Name; status='PASS' }) }
    catch { $results.Add([pscustomobject]@{ name=$Name; status='FAIL'; detail="$_" }); throw }
}
function Screenshot([string]$Name) {
    $path = Join-Path $artifacts $Name
    UI @('screenshot', '--output', $path) | Out-Null
    if (-not (Test-Path -LiteralPath $path)) { throw 'Screenshot missing' }
    $screenshots.Add($path)
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVoxTypeUiActivation {
 [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}
public static class VoxTypeUiActivation {
 public static uint Activate(string id, string arguments) {
  var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));
  uint processId;
  try { Marshal.ThrowExceptionForHR(((IVoxTypeUiActivation)obj).ActivateApplication(id, arguments, 0, out processId)); return processId; }
  finally { Marshal.ReleaseComObject(obj); }
 }
}
'@
try {
    if (-not (Test-Path -LiteralPath $ImportModelPath)) { throw 'Import fixture missing' }
    $ImportModelPath = [IO.Path]::GetFullPath($ImportModelPath)
    UI @('invoke', 'NavRecognition') | Out-Null
    $mainWindow = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -ne 'PopupHost' | Select-Object -First 1
    $script:mainHwnd = $mainWindow.hwnd
    UI @('invoke', 'RefreshModels') | Out-Null
    UI @('invoke', 'ModelDetails') | Out-Null
    $managedPath = (UI @('get-value', 'ManagedModelPath', '--json') | ConvertFrom-Json).text
    # The whole batch may delete only this explicitly isolated developer profile model.
    $managedPath = [IO.Path]::GetFullPath($managedPath)
    if ($managedPath -notmatch ('[\\/]development-profiles[\\/]' + [regex]::Escape($ProfileId) + '[\\/]')) { throw 'App does not use the isolated model directory' }
    UI @('invoke', 'ModelDetails') | Out-Null
    Test 'Missing model shows safe command states' {
        UI @('wait-for', 'ModelInstallStatus', '--value', '未安装', '--contains', '-t', '3000')
        UI @('wait-for', 'DownloadModel', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
        UI @('wait-for', 'UseModel', '-p', 'IsEnabled', '--value', 'False', '-t', '3000')
        UI @('wait-for', 'DeleteModel', '-p', 'IsEnabled', '--value', 'False', '-t', '3000')
        Screenshot '01-missing.png'
    }
    Test 'Mirror is explicitly selected' {
        UI @('scroll-into-view', 'ModelDownloadSource')
        UI @('invoke', 'ModelDownloadSource', '--action', 'expand')
        $popup = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'PopupHost' | Select-Object -First 1
        if (-not $popup) { throw 'Source popup not found' }
        $tree = WindowUI "$($popup.hwnd)" @('inspect', '--interactive', '--json') | ConvertFrom-Json
        $option = @(Walk $tree.windows[0].elements) | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'HF-Mirror（第三方镜像）' } | Select-Object -First 1
        if (-not $option) { throw 'Mirror option not found in live tree' }
        WindowUI "$($popup.hwnd)" @('invoke', $option.selector, '--action', 'select')
        UI @('wait-for', 'ModelDownloadSource', '--value', 'HF-Mirror', '--contains', '-t', '3000')
    }
    Test 'Cancel cleans downloaded temporary files and enables retry' {
        UI @('scroll-into-view', 'DownloadModel'); UI @('invoke', 'DownloadModel')
        UI @('wait-for', 'CancelModelDownload', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
        Screenshot '02-downloading.png'
        UI @('invoke', 'CancelModelDownload')
        UI @('wait-for', 'ModelInstallStatus', '--value', '已取消', '--contains', '-t', '10000')
        if (Test-Path -LiteralPath $managedPath) { throw 'Cancelled download published final model' }
        if (@(Get-ChildItem -LiteralPath (Split-Path $managedPath) -Filter '*.download').Count) { throw 'Cancelled download left temporary files' }
    }
    Test 'Download continues with settings hidden and installs after verification' {
        UI @('invoke', 'DownloadModel'); UI @('invoke', 'Close')
        if (@(UI @('list-windows', '--json') | ConvertFrom-Json).Count) { throw 'Settings not hidden' }
        [VoxTypeUiActivation]::Activate($AppUserModelId, '--settings') | Out-Null
        UI @('wait-for', 'ModelInstallStatus', '--value', '已安装', '--contains', '-t', '45000')
        UI @('wait-for', 'UseModel', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
        if (-not (Test-Path -LiteralPath $managedPath)) { throw 'Installed model missing in actual package model directory' }
        if ((Get-FileHash -LiteralPath $managedPath -Algorithm SHA256).Hash -ne '60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE') { throw 'Installed hash incorrect' }
        Screenshot '03-installed.png'
    }
    Test 'Use model saves selection' {
        UI @('invoke', 'UseModel')
        UI @('wait-for', 'ModelInstallStatus', '--value', '已启用', '--contains', '-t', '5000')
        UI @('wait-for', 'SettingsStatus', '--value', '已保存本地模型选择', '--contains', '-t', '3000')
        $modelField = UI @('get-value', 'ModelPath', '--json') | ConvertFrom-Json
        if ($modelField.text -ne $managedPath) { throw 'Selected model path differs' }
    }
    Test 'Deletion requires confirmation and cancellation preserves model' {
        UI @('scroll-into-view', 'DeleteModel'); UI @('invoke', 'DeleteModel')
        UI @('wait-for', '取消', '-t', '3000')
        Screenshot '04-delete-confirmation.png'
        UI @('invoke', '取消')
        UI @('wait-for', 'DeleteModel', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
        if (-not (Test-Path -LiteralPath $managedPath)) { throw 'Confirmation cancel deleted model' }
    }
    Test 'Confirmed deletion removes only profile model' {
        UI @('invoke', 'DeleteModel'); UI @('wait-for', '删除模型', '-t', '3000'); UI @('invoke', '删除模型')
        UI @('wait-for', 'ModelInstallStatus', '--value', '已删除', '--contains', '-t', '5000')
        if (Test-Path -LiteralPath $managedPath) { throw 'Deletion did not remove model' }
        if (-not (Test-Path -LiteralPath $ImportModelPath)) { throw 'External fixture deleted' }
    }
    Test 'Native picker imports verified model and preserves source' {
        UI @('invoke', 'ImportModel')
        $picker = $null
        for ($attempt = 0; $attempt -lt 10 -and -not $picker; $attempt++) {
            $picker = @(Checked @('ui', 'list-windows', '--on', 'local', '--json') | ConvertFrom-Json) | Where-Object { $_.ownerHwnd -eq $mainWindow.hwnd -and $_.className -eq '#32770' } | Select-Object -First 1
            if (-not $picker) { Start-Sleep -Milliseconds 200 }
        }
        if (-not $picker) { throw 'Import picker missing' }
        $tree = WindowUI "$($picker.hwnd)" @('inspect', '--interactive', '--json') | ConvertFrom-Json
        $filename = @(Walk $tree.windows[0].elements) | Where-Object { $_.type -eq 'Edit' -and ($_.automationId -eq 'FileNameControlHost' -or $_.name -match '^File name') } | Select-Object -First 1
        $open = @(Walk $tree.windows[0].elements) | Where-Object { $_.type -eq 'SplitButton' -and $_.automationId -eq '1' -and $_.name -eq 'Open' } | Select-Object -First 1
        if (-not $filename -or -not $open) { throw 'Picker filename/open controls not found in live tree' }
        WindowUI "$($picker.hwnd)" @('set-value', $filename.selector, $ImportModelPath)
        WindowUI "$($picker.hwnd)" @('invoke', $open.selector)
        UI @('wait-for', 'ModelInstallStatus', '--value', '已安装', '--contains', '-t', '10000')
        if (-not (Test-Path -LiteralPath $ImportModelPath) -or -not (Test-Path -LiteralPath $managedPath)) { throw 'Import lost source or destination' }
        Screenshot '05-imported.png'
    }
    Test 'Open folder succeeds' {
        UI @('invoke', 'OpenModelFolder')
        UI @('wait-for', 'RefreshModels', '-p', 'IsEnabled', '--value', 'True', '-t', '5000')
        $status = UI @('get-value', 'ModelInstallStatus', '--json') | ConvertFrom-Json
        if ($status.text -match '无法打开') { throw 'Folder launch failed' }
    }
} finally {
    Checked @('ui', 'yield', '--on', 'local') | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow
    $report = [ordered]@{ target='local'; appPid=$AppPid; profile=$ProfileId; results=@($results.ToArray()); screenshots=@($screenshots.ToArray()) }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'result.json') -Encoding utf8
    Write-Host "Report: $artifacts/result.json"
}
Write-Host "Passed: $($results.Count) | Failed: 0"
