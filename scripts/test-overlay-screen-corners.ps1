param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$AppUserModelId)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/screen-corners-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
Add-Type -AssemblyName System.Drawing
$source = Get-Content (Join-Path $PSScriptRoot 'test-overlay-interaction.ps1') -Raw
Add-Type ([regex]::Match($source, "(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RoundScreenProbe {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct WindowClass {
  public uint Style; public IntPtr Proc; public int ClassExtra, WindowExtra;
  public IntPtr Instance, Icon, Cursor, Background;
  public string MenuName, ClassName;
 }
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
 delegate IntPtr WindowProc(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
 static readonly WindowProc procedure=DefWindowProc;
 static IntPtr brush, instance;
 const string ClassName="VoxTypeScreenCornerProbe";
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
 [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
 [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ushort RegisterClass(ref WindowClass value);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool UnregisterClass(string name,IntPtr module);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string type,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr module,IntPtr data);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect value);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr window);
 [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("dwmapi.dll")] public static extern int DwmFlush();
 public static Rect Bounds(IntPtr window) { Rect r; if(!GetWindowRect(window,out r))throw new InvalidOperationException("Window bounds unavailable"); return r; }
 public static IntPtr Create() {
  instance=GetModuleHandle(null); brush=CreateSolidBrush(0x00522042); // RGB(66,32,82)
  var value=new WindowClass { Proc=Marshal.GetFunctionPointerForDelegate(procedure),Instance=instance,Background=brush,ClassName=ClassName };
  if(RegisterClass(ref value)==0)throw new System.ComponentModel.Win32Exception();
  return CreateWindowEx(0x08000088,ClassName,"VoxType corner test background",0x80000000,0,0,10,10,IntPtr.Zero,IntPtr.Zero,instance,IntPtr.Zero);
 }
 public static void PlaceBehind(IntPtr background,IntPtr overlay) {
  var r=Bounds(overlay);
  SetWindowPos(background,overlay,r.Left-16,r.Top-16,r.Right-r.Left+32,r.Bottom-r.Top+32,0x50);
  UpdateWindow(background);
 }
 public static void Close(IntPtr window) { DestroyWindow(window); UnregisterClass(ClassName,instance); DeleteObject(brush); }
}
'@
function UI([string[]]$Arguments) {
    $output = & winapp ui @Arguments -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $output" }
    $output
}
function Activate([string]$Arguments) { [OverlayInteractionProbe]::Activate($AppUserModelId, $Arguments) | Out-Null }
function Diagnostic {
    $path = Join-Path $artifacts ([guid]::NewGuid().ToString('N') + '.json')
    Activate "--check-diagnostics=$path"
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path $path)) { if ([DateTime]::UtcNow -gt $deadline) { throw 'Diagnostics timed out' }; Start-Sleep -Milliseconds 25 }
    Get-Content $path -Raw | ConvertFrom-Json
}
function SelectTheme([string]$Theme) {
    UI @('invoke', 'ThemeSelector', '-w', "$($script:main.hwnd)") | Out-Null
    $found = UI @('search', $Theme, '--json') | ConvertFrom-Json
    $item = $found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Theme } | Select-Object -First 1
    if (-not $item) { throw "Theme option missing: $Theme" }
    UI @('invoke', $item.selector) | Out-Null
}
function ShowFailure {
    Activate '--check-overlay=failed-rejected'
    UI @('wait-for', 'OverlayStatus', '--value', '请求被拒绝', '-t', '2000') | Out-Null
    $script:overlay = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
}
function SetPinned([bool]$Value) {
    if ($script:pinned -eq $Value) { return }
    UI @('invoke', 'OverlayPinnedSetting', '-w', "$($script:main.hwnd)") | Out-Null
    $script:pinned = $Value
}
$results = [Collections.Generic.List[object]]::new()
function Capture([string]$Name) {
    $old = [RoundScreenProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try {
        [RoundScreenProbe]::PlaceBehind($script:background, [IntPtr]$script:overlay.hwnd)
        [RoundScreenProbe]::DwmFlush() | Out-Null
        $r = [RoundScreenProbe]::Bounds([IntPtr]$script:overlay.hwnd)
        $w=$r.Right-$r.Left; $h=$r.Bottom-$r.Top
        $bitmap = [Drawing.Bitmap]::new($w+32,$h+32)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($r.Left-16,$r.Top-16,0,0,[Drawing.Size]::new($w+32,$h+32))
            $bitmap.Save((Join-Path $artifacts "$Name.png"),[Drawing.Imaging.ImageFormat]::Png)
            $corners = foreach ($point in @(@(16,16),@(($w+15),16),@(16,($h+15)),@(($w+15),($h+15)))) {
                $c=$bitmap.GetPixel($point[0],$point[1])
                [pscustomobject]@{ R=[int]$c.R; G=[int]$c.G; B=[int]$c.B }
            }
            $wrong = @($corners | Where-Object { [Math]::Abs($_.R-66) -gt 3 -or [Math]::Abs($_.G-32) -gt 3 -or [Math]::Abs($_.B-82) -gt 3 })
            $center=$bitmap.GetPixel(16+[int]($w/2),16+[int]($h/2))
            $visible=[Math]::Abs([int]$center.R-66) -gt 10 -or [Math]::Abs([int]$center.G-32) -gt 10 -or [Math]::Abs([int]$center.B-82) -gt 10
            $results.Add([pscustomobject]@{ name=$Name; status=$(if($wrong.Count -or -not $visible){'FAIL'}else{'PASS'}); visible=$visible; corners=@($corners) })
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    } finally { [RoundScreenProbe]::SetThreadDpiAwarenessContext($old) | Out-Null }
}
$initial = Diagnostic
if ($initial.IsBusy) { throw 'Real recording active; leave it untouched' }
$script:pinned = $initial.OverlayPresentation.Pinned
$background=[IntPtr]::Zero
try {
    Activate '--settings'
    UI @('wait-for', 'NavGeneral', '-t', '2000') | Out-Null
    $script:main = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 设置' | Select-Object -First 1
    UI @('invoke', 'NavGeneral', '-w', "$($main.hwnd)") | Out-Null
    SetPinned $false
    $oldDpi=[RoundScreenProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try { $script:background=[RoundScreenProbe]::Create() }
    finally { [RoundScreenProbe]::SetThreadDpiAwarenessContext($oldDpi) | Out-Null }
    SelectTheme 'dark'
    ShowFailure
    Capture 'dark-visible'
    Activate '--check-overlay=resume'
    ShowFailure
    Capture 'dark-same-theme-reshow'
    SelectTheme 'light'
    Capture 'light-after-settings'
    Start-Sleep -Milliseconds 1000
    Capture 'light-after-settings-settled'
    Activate '--check-overlay=resume'
    ShowFailure
    Capture 'light-same-theme-reshow'
    Activate '--check-overlay=resume'
    SelectTheme 'dark'
    SelectTheme 'light'
    ShowFailure
    Capture 'light-first-show-after-hidden-switch'
    Start-Sleep -Milliseconds 1000
    Capture 'light-first-show-settled'
    SetPinned $true
    Activate '--check-overlay=resume'
    UI @('wait-for', 'StartRecording', '-t', '2000') | Out-Null
    Capture 'light-idle-entry'
    SetPinned $false
    SetPinned $true
    UI @('wait-for', 'StartRecording', '-t', '2000') | Out-Null
    Capture 'light-idle-entry-reshow'
    SetPinned $false
    ShowFailure
    SelectTheme 'dark'
    Capture 'dark-roundtrip'
} finally {
    if ($background -ne [IntPtr]::Zero) { [RoundScreenProbe]::Close($background) }
    if ($main) { SetPinned $initial.OverlayPresentation.Pinned; SelectTheme $initial.PreviewTheme }
    Activate '--check-overlay=resume'
    if ($main) { UI @('invoke', 'Close', '-w', "$($main.hwnd)") | Out-Null }
    $results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $artifacts 'results.json') -Encoding utf8
    $results | Select-Object name,status | Format-Table -AutoSize
    Write-Output "Artifacts: $artifacts"
}
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
