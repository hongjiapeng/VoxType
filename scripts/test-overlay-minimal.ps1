param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$AppUserModelId
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/minimal-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$results = [Collections.Generic.List[object]]::new()
$captures = [Collections.Generic.List[object]]::new()
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMinimalOverlayActivation {
 [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
}
public static class MinimalOverlayProbe {
 public static uint Activate(string id,string args) {
  var obj=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));
  try { uint pid; Marshal.ThrowExceptionForHR(((IMinimalOverlayActivation)obj).ActivateApplication(id,args,0,out pid)); return pid; }
  finally { Marshal.ReleaseComObject(obj); }
 }
}
'@
function Activate([string]$Arguments) { [MinimalOverlayProbe]::Activate($AppUserModelId, $Arguments) | Out-Null }
function Diagnostic {
    $path = Join-Path $artifacts ([guid]::NewGuid().ToString('N') + '.json')
    Activate "--check-diagnostics=$path"
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while (-not (Test-Path -LiteralPath $path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Debug diagnostics timed out' }
        Start-Sleep -Milliseconds 10
    }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Preview([string]$State, [string]$Theme = 'dark') {
    Activate "--check-overlay=$State --check-theme=$Theme"
    Start-Sleep -Milliseconds 400
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function UI([string[]]$Arguments) {
    $output = & winapp ui @Arguments -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $output" }
    $output
}
function Overlay {
    @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
}
function Screenshot([string]$Name) {
    $path = Join-Path $artifacts "$Name.png"
    # PrintWindow can return a transparent frame during a compositor resize.
    # Require a painted capsule, rather than accepting an empty screenshot as evidence.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        UI @('screenshot', '-w', "$((Overlay).hwnd)", '-o', $path) | Out-Null
        $bitmap = [Drawing.Bitmap]::new($path)
        try { $painted = $bitmap.GetPixel([int]($bitmap.Width/2), [int]($bitmap.Height/2)).A -ge 250 }
        finally { $bitmap.Dispose() }
        if ($painted) { $captures.Add([pscustomobject]@{ name=$Name; attempts=$attempt }); return }
        Start-Sleep -Milliseconds 250
    }
    throw "Capsule was not painted in screenshot $Name"
}
function Elements($Nodes) {
    foreach ($node in $Nodes) {
        $node
        if ($node.children) { Elements $node.children }
    }
}
function Inspect {
    $tree = UI @('inspect', '-w', "$((Overlay).hwnd)", '--json') | ConvertFrom-Json
    @(Elements $tree.windows[0].elements)
}
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body | Out-Null; $results.Add([pscustomobject]@{ name=$Name; status='PASS' }) }
    catch { $results.Add([pscustomobject]@{ name=$Name; status='FAIL'; detail="$_" }) }
}
$initial = Diagnostic
if ($initial.IsBusy) { throw 'A real recording is active; leave it untouched.' }
try {
    Test 'Shortcut recording has one line and no action buttons' {
        Preview 'listening'
        $elements = Inspect
        Assert (@($elements | Where-Object automationId -in @('StopRecording', 'OverlayMore', 'OverlayDetail')).Count -eq 0) 'Redundant shortcut controls are visible'
        Assert (($elements | Where-Object automationId -eq 'OverlayStatus').name -eq '松开结束') 'Missing release hint'
        Screenshot 'shortcut-listening'
    }
    Test 'Live preview is the only text line and all five waveform bars fit' {
        Preview 'preview'
        $d = Diagnostic
        $p = $d.OverlayPresentation
        Assert ($p.Title -eq '测试，测试。') 'Preview does not replace the status label'
        Assert ($p.Bars.Count -eq 5) 'Expected five waveform bars'
        foreach ($bar in $p.Bars) {
            Assert ($bar.X -ge -0.5 -and $bar.X + $bar.Width -le $p.WaveformWidth + 0.5) 'Waveform bar is clipped horizontally'
            Assert ($bar.Y -ge -0.5 -and $bar.Y + $bar.Height -le $p.WaveformHeight + 0.5) 'Waveform bar is clipped vertically'
        }
        Assert ($p.Width -lt 320 -and $p.Height -le 56) 'Short preview is unnecessarily large'
        Screenshot 'preview-dark'
        Preview 'preview' 'light'
        Screenshot 'preview-light'
        Preview 'preview' 'dark'
        Screenshot 'preview-dark-roundtrip'
    }
    Test 'Long preview remains one line and bounded' {
        Preview 'preview-long'
        $p = (Diagnostic).OverlayPresentation
        Assert ($p.Title.StartsWith('…') -and $p.Title.EndsWith('。')) 'Long preview does not retain the newest phrase'
        Assert ($p.Width -le 420 -and $p.Height -le 56) 'Long preview exceeds the compact bounds'
        Screenshot 'preview-long'
    }
    Test 'Click recording retains working stop and more controls' {
        Preview 'tray-preview'
        $elements = Inspect
        Assert (@($elements | Where-Object automationId -eq 'StopRecording').Count -eq 1) 'Click recording needs a stop button'
        Assert (@($elements | Where-Object automationId -eq 'OverlayMore').Count -eq 1) 'Click recording is missing options'
        Screenshot 'click-preview'
        UI @('invoke', 'StopRecording', '-w', "$((Overlay).hwnd)") | Out-Null
        Assert ((Diagnostic).OverlayPresentation.Title -eq '结束按钮检查通过') 'Stop command was not invoked'
    }
    Test 'Release hides immediately then shows compact processing feedback' {
        Preview 'preview'
        Activate '--check-overlay=release --check-theme=dark'
        $d = Diagnostic
        Assert (-not $d.OverlayVisible -and $d.OverlayPresentation.DelayProcessing) 'Release did not hide the recording bar'
        Start-Sleep -Milliseconds 400
        $d = Diagnostic
        Assert ($d.OverlayVisible -and $d.OverlayPresentation.Title -eq '识别中…') 'Delayed processing feedback missing'
        Assert (-not $d.OverlayPresentation.ShowMore -and -not $d.OverlayPresentation.ShowStop) 'Processing shows unnecessary actions'
        Screenshot 'processing'
    }
    Test 'Fast completion cancels delayed feedback and delivery hides the bar' {
        Activate '--check-overlay=release --check-theme=dark'
        Activate '--check-overlay=injecting --check-theme=dark'
        Start-Sleep -Milliseconds 500
        $d = Diagnostic
        Assert (-not $d.OverlayVisible) 'Stale processing timer appeared over text delivery'
        Activate '--check-overlay=completed --check-theme=dark'
        $d = Diagnostic
        Assert ($d.OverlayPresentation.IsStandby -and $d.OverlayVisible -eq $d.OverlayPresentation.Pinned) 'Success did not return directly to standby or hide'
    }
    Test 'Failure retains retry and options' {
        Preview 'failed-long'
        $elements = Inspect
        Assert (@($elements | Where-Object automationId -eq 'RetryRecording').Count -eq 1) 'Failure has no retry action'
        Assert (@($elements | Where-Object automationId -eq 'OverlayMore').Count -eq 1) 'Failure has no options'
        Assert (@($elements | Where-Object type -eq 'Text').Count -eq 1) 'Failure still has a second detail line'
        Screenshot 'failure'
    }
    Test 'Rejected request stays a compact capsule with round ends in both themes' {
        foreach ($theme in @('light', 'dark')) {
            Preview 'failed-rejected' $theme
            $p = (Diagnostic).OverlayPresentation
            Assert ($p.Title -eq '请求被拒绝' -and $p.Width -le 300 -and $p.Height -le 56) 'Rejected request stretched the capsule'
            Assert ([Math]::Abs($p.Radius - $p.Height/2) -le 1) 'Capsule no longer has semicircular ends'
            $elements = Inspect
            Assert (@($elements | Where-Object type -eq 'Text').Count -eq 1) 'Full error details leaked into the capsule'
            Screenshot "rejected-$theme"
            $bitmap = [Drawing.Bitmap]::new((Join-Path $artifacts "rejected-$theme.png"))
            try {
                foreach ($point in @(@(0,0),@(($bitmap.Width-1),0),@(0,($bitmap.Height-1)),@(($bitmap.Width-1),($bitmap.Height-1)))) {
                    Assert ($bitmap.GetPixel($point[0],$point[1]).A -eq 0) 'Failure capsule has a rectangular corner or backing'
                }
            } finally { $bitmap.Dispose() }
        }
    }
    Test 'Completion closes click recording options along with the bar' {
        Preview 'menu-completed'
        $overlayHwnd = (Overlay).hwnd
        UI @('invoke', 'OverlayMore', '-w', "$overlayHwnd") | Out-Null
        $popup = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object {
            $_.title -eq 'PopupHost' -and $_.ownerHwnd -eq $overlayHwnd
        } | Select-Object -First 1
        Assert ($null -ne $popup) 'Recording options popup did not open'
        $opened = UI @('search', 'OverlayPin', '-w', "$($popup.hwnd)", '--json') | ConvertFrom-Json
        Assert (@($opened.matches | Where-Object automationId -eq 'OverlayPin').Count -eq 1) 'Recording options did not open'
        Start-Sleep -Milliseconds 2800
        # App-scoped search chooses the foreground settings window and can match
        # OverlayPinnedSetting. Track the actual menu popup instead.
        $remaining = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object hwnd -eq $popup.hwnd
        Assert (@($remaining).Count -eq 0) 'Recording options stayed open'
        $d = Diagnostic
        Assert ($d.OverlayPresentation.IsStandby -and $d.OverlayVisible -eq $d.OverlayPresentation.Pinned) 'Menu prevented completion from clearing the recording UI'
    }
} finally {
    $restoreTheme = if ($initial.PreviewTheme) { $initial.PreviewTheme } else { $initial.Theme }
    Activate "--check-overlay=resume --check-theme=$restoreTheme"
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
    $captures | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'captures.json') -Encoding utf8
    $results | Format-Table -AutoSize
    Write-Output "Artifacts: $artifacts"
}
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
