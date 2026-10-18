param(
    [int]$AppPid = 0,
    [Parameter(Mandatory)][string]$AppUserModelId
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/overlay-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.Drawing
function Checked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    $output
}
function UI([string[]]$Arguments) { Checked (@('ui') + $Arguments + @('-a', "$AppPid")) }
function Walk($Nodes) { foreach ($node in $Nodes) { $node; Walk $node.children } }
Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOverlayPreviewActivation {
 [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}
public static class OverlayPreviewActivation {
 [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo {
  public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
  public int Left, Top, Right, Bottom;
 }
 [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
 public static IntPtr ForegroundFocus() {
  var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
  if (!GetGUIThreadInfo(0, ref info)) {
   if (GetForegroundWindow() == IntPtr.Zero) return IntPtr.Zero;
   throw new InvalidOperationException("Foreground focus unavailable");
  }
  return info.Focus;
 }
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
 public static uint WindowProcess(IntPtr hwnd) { uint processId; GetWindowThreadProcessId(hwnd, out processId); return processId; }
 [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetStyle(IntPtr hwnd, int index);
 [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int a, int b, int c, int d);
 [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr region);
 [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 public static bool HasNoWindowRegion(IntPtr hwnd) {
  var oldContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
  var region = CreateRectRgn(0, 0, 0, 0);
  try { return GetWindowRgn(hwnd, region) == 0; }
  finally { DeleteObject(region); SetThreadDpiAwarenessContext(oldContext); }
 }
 public static void Activate(string id, string arguments) {
  ActivateWithPid(id, arguments);
 }
 public static uint ActivateWithPid(string id, string arguments) {
  var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));
  try { uint processId; Marshal.ThrowExceptionForHR(((IOverlayPreviewActivation)obj).ActivateApplication(id, arguments, 0, out processId)); return processId; }
  finally { Marshal.ReleaseComObject(obj); }
 }
}
'@
try {
    $project = [xml](Get-Content -LiteralPath (Join-Path $root 'src/VoxType.App/VoxType.App.csproj') -Raw)
    $framework = $project.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText
    $outputPath = Join-Path $root "src/VoxType.App/bin/x64/Debug/$framework/win-x64"
    $family = $AppUserModelId.Split('!')[0]
    $package = Get-AppxPackage | Where-Object PackageFamilyName -eq $family | Select-Object -First 1
    if (-not $package) { throw 'Development package is not registered; run winapp run first' }
    foreach ($assembly in @('VoxType.App.dll', 'VoxType.Infrastructure.dll')) {
        $built = Get-FileHash -LiteralPath (Join-Path $outputPath $assembly)
        $deployed = Get-FileHash -LiteralPath (Join-Path $package.InstallLocation $assembly)
        if ($built.Hash -ne $deployed.Hash) { throw "Stale packaged binary: $assembly. Exit the fixture, then refresh with winapp run" }
    }
    $results.Add([pscustomobject]@{ name='deployed-binaries'; status='PASS' })
    if ($AppPid -eq 0) {
        $foreground = [OverlayPreviewActivation]::GetForegroundWindow()
        $keyboardFocus = [OverlayPreviewActivation]::ForegroundFocus()
        # winapp run registers the development package before this script. Launch the
        # registered AUMID directly, avoiding changes to its layout while an app is running.
        $AppPid = [int][OverlayPreviewActivation]::ActivateWithPid($AppUserModelId, '--check-overlay=voice --check-theme=dark')
        if ($AppPid -le 0) { throw 'Cold launch did not return a PID' }
        UI @('wait-for', 'OverlayStatus', '--value', '正在聆听', '-t', '3000') | Out-Null
        if ([OverlayPreviewActivation]::GetForegroundWindow() -ne $foreground -or [OverlayPreviewActivation]::ForegroundFocus() -ne $keyboardFocus) {
            throw 'Cold start changed the foreground window or its keyboard focus'
        }
        $results.Add([pscustomobject]@{ name='cold-start-focus'; status='PASS' })
    }
    foreach ($theme in @('dark', 'light')) {
        foreach ($state in @('listening', 'voice', 'preview', 'preview-long', 'processing', 'injecting', 'completed', 'failed', 'failed-short', 'failed-long', 'tray')) {
            $name = "$theme-$state"
            try {
                $foreground = [OverlayPreviewActivation]::GetForegroundWindow()
                $keyboardFocus = [OverlayPreviewActivation]::ForegroundFocus()
                [OverlayPreviewActivation]::Activate($AppUserModelId, "--check-overlay=$state --check-theme=$theme")
                $title = switch ($state) {
                    'processing' { '正在识别' }
                    'completed' { '已发送文本' }
                    'injecting' { '正在发送文本' }
                    'failed' { '没有听到声音' }
                    'failed-short' { '录音太短' }
                    'failed-long' { '文本已复制' }
                    default { '正在聆听' }
                }
                UI @('wait-for', 'OverlayStatus', '--value', $title, '-t', '3000') | Out-Null
                # Inspect after entrance and any text-scale height adjustment settle.
                Start-Sleep -Milliseconds 350
                $afterForeground = [OverlayPreviewActivation]::GetForegroundWindow()
                if ($afterForeground -ne $foreground) { throw "Preview foreground changed: $foreground -> $afterForeground (process $([OverlayPreviewActivation]::WindowProcess($afterForeground)); overlay $AppPid)" }
                if ([OverlayPreviewActivation]::ForegroundFocus() -ne $keyboardFocus) { throw 'Preview changed keyboard focus in the foreground application' }
                $window = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
                if (-not $window) { throw 'Overlay window missing' }
                $style = [OverlayPreviewActivation]::GetStyle([IntPtr]$window.hwnd, -20).ToInt64()
                if (($style -band 0x08000000) -eq 0 -or ($style -band 0x80) -eq 0 -or ($style -band 0x40000) -ne 0) { throw 'Overlay lost its non-activating tool-window styles' }
                $tree = UI @('inspect', '--json') | ConvertFrom-Json
                $tree | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $artifacts "$name.json") -Encoding utf8
                $elements = @(Walk $tree.windows[0].elements)
                $status = @($elements | Where-Object automationId -eq 'OverlayStatus')
                if ($status.Count -ne 1 -or $status[0].isOffscreen) { throw 'Status is missing or offscreen' }
                $detail = @($elements | Where-Object automationId -eq 'OverlayDetail')
                $hasDetail = $state -notin @('injecting', 'completed')
                if ($hasDetail -and ($detail.Count -ne 1 -or $detail[0].isOffscreen)) { throw 'Detail is missing or offscreen' }
                if (-not $hasDetail -and @($detail | Where-Object { -not $_.isOffscreen }).Count -gt 0) { throw 'Delivery still shows redundant detail' }
                $pane = $tree.windows[0].elements[0]
                if (-not [OverlayPreviewActivation]::HasNoWindowRegion([IntPtr]$window.hwnd)) { throw 'Binary native clipping still overrides the antialiased XAML outline' }
                $scale = [OverlayPreviewActivation]::GetDpiForWindow([IntPtr]$window.hwnd) / 96.0
                if ([Math]::Abs($pane.width - 320 * $scale) -gt 1) { throw 'Capsule width changed with its content' }
                if ($status[0].y -lt $pane.y + 8 * $scale) { throw 'Title reaches the top padding' }
                if ($hasDetail) {
                    if ($detail[0].y + $detail[0].height -gt $pane.y + $pane.height - 8 * $scale) { throw 'Detail reaches the bottom padding' }
                    if ($detail[0].y -lt $status[0].y + $status[0].height) { throw 'Title and detail overlap' }
                    if ($detail[0].height -gt 22 * $scale) { throw 'Detail wraps instead of staying compact' }
                }
                if ($state -eq 'failed' -and $detail[0].name -ne '检查麦克风后重试') { throw 'Silence error lacks a recovery action' }
                if ($state -eq 'tray') {
                    UI @('invoke', 'StopRecording') | Out-Null
                    UI @('wait-for', 'OverlayStatus', '--value', '结束按钮检查通过', '-t', '2000') | Out-Null
                    Start-Sleep -Milliseconds 350
                    $afterForeground = [OverlayPreviewActivation]::GetForegroundWindow()
                    if ($afterForeground -ne $foreground) { throw "Stop foreground changed: $foreground -> $afterForeground (process $([OverlayPreviewActivation]::WindowProcess($afterForeground)); overlay $AppPid)" }
                    if ([OverlayPreviewActivation]::ForegroundFocus() -ne $keyboardFocus) { throw 'Stop command changed keyboard focus' }
                }
                $path = Join-Path $artifacts "$name.png"
                Checked @('ui', 'screenshot', '-w', "$($window.hwnd)", '--output', $path) | Out-Null
                if (-not (Test-Path -LiteralPath $path)) { throw 'Screenshot missing' }
                $bitmap = [Drawing.Bitmap]::new($path)
                try {
                    $colors = [Collections.Generic.HashSet[int]]::new()
                    for ($x = 4; $x -lt $bitmap.Width; $x += 8) {
                        for ($y = 4; $y -lt $bitmap.Height; $y += 8) { [void]$colors.Add($bitmap.GetPixel($x, $y).ToArgb()) }
                    }
                    if ($colors.Count -lt 4) { throw 'Blank capture rejected: UIA alone does not establish visual readiness' }
                } finally { $bitmap.Dispose() }
                $screenshots.Add($path)
                $results.Add([pscustomobject]@{ name=$name; status='PASS' })
            } catch { $results.Add([pscustomobject]@{ name=$name; status='FAIL'; detail="$_" }) }
        }
    }
    try {
        [OverlayPreviewActivation]::Activate($AppUserModelId, '--check-overlay=listening --check-theme=dark')
        UI @('wait-for', 'OverlayStatus', '--value', '正在聆听', '-t', '5000') | Out-Null
        $window = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
        $video = Join-Path $artifacts 'motion.mp4'
        $capture = Start-Process -FilePath (Get-Command winapp).Source -WindowStyle Hidden -PassThru `
            -ArgumentList @('ui', 'record', '-w', "$($window.hwnd)", '--duration-sec', '3', '--fps', '30', '--frames', '--output', $video, '--json') `
            -RedirectStandardOutput (Join-Path $artifacts 'motion-result.json') -RedirectStandardError (Join-Path $artifacts 'motion-error.txt')
        Start-Sleep -Milliseconds 600
        [OverlayPreviewActivation]::Activate($AppUserModelId, '--check-overlay=motion --check-theme=dark')
        if (-not $capture.WaitForExit(15000)) { throw 'Motion capture did not finish in time' }
        if ($capture.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $video)) { throw 'Motion capture failed; inspect motion-error.txt' }
        $motion = Get-Content -LiteralPath (Join-Path $artifacts 'motion-result.json') -Raw | ConvertFrom-Json
        if ($motion.frameArtifacts.images -lt 4) { throw 'Motion recording has no meaningful frame changes' }
        $results.Add([pscustomobject]@{ name='motion-capture'; status='PASS' })
    } catch { $results.Add([pscustomobject]@{ name='motion-capture'; status='FAIL'; detail="$_" }) }
    try {
        [OverlayPreviewActivation]::Activate($AppUserModelId, '--check-overlay=failed-long --check-theme=dark')
        UI @('wait-for', 'OverlayStatus', '--value', '文本已复制', '-t', '5000') | Out-Null
        Start-Sleep -Milliseconds 350
        $window = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
        $video = Join-Path $artifacts 'resize.mp4'
        $capture = Start-Process -FilePath (Get-Command winapp).Source -WindowStyle Hidden -PassThru `
            -ArgumentList @('ui', 'record', '-w', "$($window.hwnd)", '--duration-sec', '3', '--fps', '30', '--frames', '--output', $video, '--json') `
            -RedirectStandardOutput (Join-Path $artifacts 'resize-result.json') -RedirectStandardError (Join-Path $artifacts 'resize-error.txt')
        Start-Sleep -Milliseconds 600
        [OverlayPreviewActivation]::Activate($AppUserModelId, '--check-overlay=voice --check-theme=dark')
        Start-Sleep -Milliseconds 700
        [OverlayPreviewActivation]::Activate($AppUserModelId, '--check-overlay=failed-long --check-theme=dark')
        if (-not $capture.WaitForExit(15000) -or $capture.ExitCode -ne 0) { throw 'Resize recording failed' }
        $resize = Get-Content -LiteralPath (Join-Path $artifacts 'resize-result.json') -Raw | ConvertFrom-Json
        # A fixed-width capsule has three settled frames: error, listening, error.
        # It should no longer produce intermediate width-animation frames.
        if ($resize.frameArtifacts.images -lt 3) { throw 'State transition recording is missing a visible state' }
        foreach ($frame in Get-ChildItem -LiteralPath $resize.frameArtifacts.directory -Filter '*.jpg') {
            $bitmap = [Drawing.Bitmap]::new($frame.FullName)
            try {
                if ([Math]::Abs($bitmap.Width - $window.width) -gt 1) { throw "Capsule width changed during transition: $($frame.Name)" }
                $colors = [Collections.Generic.HashSet[int]]::new()
                for ($x = 4; $x -lt $bitmap.Width; $x += 8) {
                    for ($y = 4; $y -lt $bitmap.Height; $y += 8) { [void]$colors.Add($bitmap.GetPixel($x, $y).ToArgb()) }
                }
                if ($colors.Count -lt 4) { throw "Blank frame during resize: $($frame.Name)" }
            } finally { $bitmap.Dispose() }
        }
        $results.Add([pscustomobject]@{ name='stable-width-capture'; status='PASS' })
    } catch { $results.Add([pscustomobject]@{ name='stable-width-capture'; status='FAIL'; detail="$_" }) }
} finally {
    try {
        $overlayWindow = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
        [OverlayPreviewActivation]::Activate($AppUserModelId, '--check-overlay=hide')
        $hideDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while ($overlayWindow -and [OverlayPreviewActivation]::IsWindowVisible([IntPtr]$overlayWindow.hwnd) -and [DateTime]::UtcNow -lt $hideDeadline) {
            Start-Sleep -Milliseconds 100
        }
        if ($overlayWindow -and [OverlayPreviewActivation]::IsWindowVisible([IntPtr]$overlayWindow.hwnd)) { throw 'Fixture overlay did not hide' }
        $results.Add([pscustomobject]@{ name='fixture-cleanup'; status='PASS' })
    } catch {
        $results.Add([pscustomobject]@{ name='fixture-cleanup'; status='FAIL'; detail="$_" })
    } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
    $failed = @($results | Where-Object status -eq 'FAIL').Count
    [ordered]@{ target='local'; appPid=$AppPid; passed=$results.Count - $failed; failed=$failed; results=@($results.ToArray()); screenshots=@($screenshots.ToArray()) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
    $results | Format-Table -AutoSize
    Write-Host "Evidence: $artifacts"
}
if ($failed) { exit 1 }
