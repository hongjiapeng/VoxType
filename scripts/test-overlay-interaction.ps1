param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$AppUserModelId,
    [Parameter(Mandatory)][string]$ProfileId,
    [switch]$SkipPhysicalInput
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/interaction-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($output -join "`n")" }
    $output
}
function UI([string[]]$Arguments) { Checked (@('ui') + $Arguments + @('-a', "$AppPid")) }
function Test([string]$Name, [scriptblock]$Body, [bool]$Physical = $false) {
    if ($Physical -and -not $script:physicalInput) {
        $results.Add([pscustomobject]@{ name=$Name; status='NOT_VERIFIED'; detail='Test editor could not acquire foreground focus; physical input validation requires an available desktop.' })
        return
    }
    try { & $Body | Out-Null; $results.Add([pscustomobject]@{ name=$Name; status='PASS' }) }
    catch { $results.Add([pscustomobject]@{ name=$Name; status='FAIL'; detail="$_" }) }
}
function Activate([string]$Arguments) { [OverlayInteractionProbe]::Activate($AppUserModelId, $Arguments) | Out-Null }
function Overlay {
    $window = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1
    if (-not $window) { throw 'Overlay window missing' }
    $window
}
function Popup {
    $windows = @(UI @('list-windows', '--json') | ConvertFrom-Json) | Where-Object title -eq 'PopupHost'
    foreach ($window in $windows) {
        # A title-bar tooltip can also be a PopupHost. Target the actual options menu.
        $search = UI @('search', 'OverlayPin', '-w', "$($window.hwnd)", '--json') | ConvertFrom-Json
        if (($search | ConvertTo-Json -Depth 15) -match 'OverlayPin') { return $window }
    }
    throw 'Options menu missing'
}
function AssertFocus {
    if ([OverlayInteractionProbe]::GetForegroundWindow() -ne $script:targetWindow -or [OverlayInteractionProbe]::Focus() -ne $script:targetFocus) {
        throw "Editor focus changed: foreground=$([OverlayInteractionProbe]::GetForegroundWindow()) expected=$targetWindow; focus=$([OverlayInteractionProbe]::Focus()) expected=$targetFocus"
    }
}
function Preview([string]$State) { Activate "--check-overlay=$State --check-theme=dark"; Start-Sleep -Milliseconds 250 }
function Screenshot([string]$Name) { UI @('screenshot', '-w', "$( (Overlay).hwnd )", '-o', (Join-Path $artifacts "$Name.png")) | Out-Null }
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOverlayInteractionActivation {
 [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
}
public static class OverlayInteractionProbe {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
 [StructLayout(LayoutKind.Sequential)] struct Gui { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public Rect Bounds; }
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref Gui info);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect bounds);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
 [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr SendMessage(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 public static IntPtr Focus() { var info = new Gui { Size=(uint)Marshal.SizeOf<Gui>() }; return GetGUIThreadInfo(0, ref info) ? info.Focus : IntPtr.Zero; }
 public static Rect Bounds(IntPtr window) { var old=SetThreadDpiAwarenessContext(new IntPtr(-4)); try { Rect bounds; GetWindowRect(window, out bounds); return bounds; } finally { SetThreadDpiAwarenessContext(old); } }
 public static void ClickEditor(IntPtr window) {
  var old=SetThreadDpiAwarenessContext(new IntPtr(-4));
  try { var r=Bounds(window); SetCursorPos((r.Left+r.Right)/2,(r.Top+r.Bottom)/2); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); Thread.Sleep(200); }
  finally { SetThreadDpiAwarenessContext(old); }
 }
 public static void Drag(IntPtr window, int dx, int dy) {
  DragFrom(window,20,dx,dy);
 }
 public static void NativeMoveLifecycle(IntPtr window,int dx,int dy) {
  var old=SetThreadDpiAwarenessContext(new IntPtr(-4));
  try { var r=Bounds(window); SendMessage(window,0x231,UIntPtr.Zero,IntPtr.Zero);
   SetWindowPos(window,IntPtr.Zero,r.Left+dx,r.Top+dy,0,0,0x15);
   SendMessage(window,0x232,UIntPtr.Zero,IntPtr.Zero); Thread.Sleep(200);
  } finally { SetThreadDpiAwarenessContext(old); }
 }
 public static void DragFrom(IntPtr window, int startXDip, int dx, int dy) {
  var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
  try { var r=Bounds(window); double scale=GetDpiForWindow(window)/96.0; int x=r.Left+(int)(startXDip*scale), y=(r.Top+r.Bottom)/2;
   SetCursorPos(x,y); mouse_event(2,0,0,0,UIntPtr.Zero);
   for(int i=1;i<=12;i++){ SetCursorPos(x+dx*i/12,y+dy*i/12); Thread.Sleep(25); }
   mouse_event(4,0,0,0,UIntPtr.Zero); Thread.Sleep(150);
  } finally { SetThreadDpiAwarenessContext(old); }
 }
 public static uint Activate(string id,string args) {
  var obj=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));
  try { uint pid; Marshal.ThrowExceptionForHR(((IOverlayInteractionActivation)obj).ActivateApplication(id,args,0,out pid)); return pid; }
  finally { Marshal.ReleaseComObject(obj); }
 }
}
'@
$fixture = $null
try {
    $script:physicalInput = $false
    if (-not $SkipPhysicalInput) {
    # Use an isolated editor with dummy text; never type into the user's applications.
    $fixturePath = Join-Path $artifacts 'editor.ps1'
    $infoPath = Join-Path $artifacts 'editor.json'
    @'
param([string]$InfoPath)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object Windows.Forms.Form
$form.Text = 'VoxType overlay acceptance fixture'
$form.StartPosition = 'Manual'
$work = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$form.Bounds = New-Object Drawing.Rectangle(($work.Left + 100), ($work.Top + 100), ($work.Width - 200), ($work.Height - 120))
$editor = New-Object Windows.Forms.TextBox
$editor.Multiline = $true
$editor.Dock = 'Bottom'
$editor.Height = 180
$editor.Text = 'Overlay test input. This text must stay visible.'
$form.Controls.Add($editor)
$form.Add_Shown({
    $form.Activate(); $editor.Focus() | Out-Null
    @{ Window=$form.Handle.ToInt64(); Focus=$editor.Handle.ToInt64() } | ConvertTo-Json | Set-Content -LiteralPath $InfoPath
})
[Windows.Forms.Application]::Run($form)
'@ | Set-Content -LiteralPath $fixturePath -Encoding utf8
    $fixture = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile', '-STA', '-File', "`"$fixturePath`"", "`"$infoPath`"") -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $infoPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $editor = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
    $script:targetWindow = [IntPtr]$editor.Window
    $script:targetFocus = [IntPtr]$editor.Focus
    [OverlayInteractionProbe]::SetForegroundWindow($targetWindow) | Out-Null
    [OverlayInteractionProbe]::ClickEditor($targetFocus)
    $script:physicalInput = [OverlayInteractionProbe]::GetForegroundWindow() -eq $targetWindow -and [OverlayInteractionProbe]::Focus() -eq $targetFocus
    }
    if (-not $script:physicalInput) {
        if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -ErrorAction SilentlyContinue; $fixture = $null }
        $script:targetWindow = [OverlayInteractionProbe]::GetForegroundWindow()
        $script:targetFocus = [OverlayInteractionProbe]::Focus()
    }
    Test 'Automatic avoidance keeps the bottom input field visible' {
        Preview 'tray'
        UI @('invoke', 'OverlayMore')
        UI @('invoke', 'OverlayResetPosition', '-w', "$( (Popup).hwnd )")
        Start-Sleep -Milliseconds 200
        $overlayRect = [OverlayInteractionProbe]::Bounds([IntPtr](Overlay).hwnd)
        $input = [OverlayInteractionProbe]::Bounds($targetFocus)
        if ($overlayRect.Left -lt $input.Right -and $overlayRect.Right -gt $input.Left -and $overlayRect.Top -lt $input.Bottom -and $overlayRect.Bottom -gt $input.Top) { throw "Overlay covers editor: overlay=$($overlayRect | ConvertTo-Json -Compress) input=$($input | ConvertTo-Json -Compress)" }
        AssertFocus
        Screenshot 'listening-avoidance'
    } -Physical $true
    Test 'Dragging moves the overlay without stealing editor focus' {
        $window = Overlay
        $before = [OverlayInteractionProbe]::Bounds([IntPtr]$window.hwnd)
        [OverlayInteractionProbe]::Drag([IntPtr]$window.hwnd, -200, -100)
        $after = [OverlayInteractionProbe]::Bounds([IntPtr]$window.hwnd)
        if ([Math]::Abs($after.Left - $before.Left + 200) -gt 2 -or [Math]::Abs($after.Top - $before.Top + 100) -gt 2) { throw "Drag offset wrong: before=$($before | ConvertTo-Json -Compress) after=$($after | ConvertTo-Json -Compress)" }
        AssertFocus
        $script:dragged = $after
    } -Physical $true
    Test 'Waveform and status text both support direct system dragging' {
        foreach ($startXDip in @(48, 130)) {
            $window = Overlay
            $before = [OverlayInteractionProbe]::Bounds([IntPtr]$window.hwnd)
            [OverlayInteractionProbe]::DragFrom([IntPtr]$window.hwnd, $startXDip, -30, -30)
            $after = [OverlayInteractionProbe]::Bounds([IntPtr]$window.hwnd)
            if ($after.Left -ne $before.Left - 30 -or $after.Top -ne $before.Top - 30) { throw "Direct dragging failed at $startXDip DIP" }
            AssertFocus
        }
    } -Physical $true
    Test 'Native caption region includes waveform and text but excludes buttons' {
        Preview 'tray'
        $window = Overlay
        $path = Join-Path $artifacts 'caption-regions.json'
        Activate "--check-diagnostics=$path"
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        while (-not (Test-Path -LiteralPath $path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        $diagnostics = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $scale = [OverlayInteractionProbe]::GetDpiForWindow([IntPtr]$window.hwnd) / 96.0
        foreach ($x in @(20, 48, 100, 170, 230)) {
            $covered = @($diagnostics.OverlayDragRegions | Where-Object {
                $x * $scale -ge $_.X -and $x * $scale -lt $_.X + $_.Width -and
                $window.height / 2 -ge $_.Y -and $window.height / 2 -lt $_.Y + $_.Height
            }).Count -gt 0
            if ($covered -ne ($x -lt 140)) { throw "Incorrect native caption hit region at $x DIP" }
        }
        AssertFocus
    }
    Test 'Native move completion saves actual position and survives state updates' {
        Preview 'voice'
        $window = Overlay
        $before = [OverlayInteractionProbe]::Bounds([IntPtr]$window.hwnd)
        [OverlayInteractionProbe]::NativeMoveLifecycle([IntPtr]$window.hwnd, -60, -60)
        $after = [OverlayInteractionProbe]::Bounds([IntPtr]$window.hwnd)
        if ($after.Left -ne $before.Left - 60 -or $after.Top -ne $before.Top - 60) { throw 'Native move completion restored the old position' }
        Preview 'processing'
        Preview 'voice'
        $restored = [OverlayInteractionProbe]::Bounds([IntPtr](Overlay).hwnd)
        if ($restored.Left -ne $after.Left -or $restored.Top -ne $after.Top) { throw 'Native drop position was not saved' }
        AssertFocus
    }
    Test 'Options menu has no directional move commands' {
        Preview 'tray'
        UI @('invoke', 'OverlayMore')
        $popup = Popup
        $tree = UI @('inspect', '-w', "$($popup.hwnd)", '--json') | ConvertFrom-Json
        $json = $tree | ConvertTo-Json -Depth 30
        if ($json -match 'OverlayMove|微调位置|向左|向右|向上|向下') { throw 'Directional adjustment commands remain' }
        UI @('invoke', 'OverlayCancel', '-w', "$($popup.hwnd)")
        Preview 'voice'
        $script:dragged = [OverlayInteractionProbe]::Bounds([IntPtr](Overlay).hwnd)
        AssertFocus
    }
    Test 'Next recording restores the dragged position' {
        Preview 'processing'
        $processing = [OverlayInteractionProbe]::Bounds([IntPtr](Overlay).hwnd)
        if ($processing.Left -ne $dragged.Left -or $processing.Top -ne $dragged.Top) { throw 'Position changed during processing' }
        Preview 'voice'
        $restored = [OverlayInteractionProbe]::Bounds([IntPtr](Overlay).hwnd)
        if ($restored.Left -ne $dragged.Left -or $restored.Top -ne $dragged.Top) { throw 'Dragged position was not restored' }
        AssertFocus
    }
    Test 'Fixed mode returns to a compact microphone entry after completion' {
        Preview 'tray'
        UI @('invoke', 'OverlayMore')
        UI @('invoke', 'OverlayPin', '-w', "$( (Popup).hwnd )")
        Preview 'auto-completed'
        UI @('wait-for', 'StartRecording', '-t', '3000')
        $window = Overlay
        $scale = [OverlayInteractionProbe]::GetDpiForWindow([IntPtr]$window.hwnd) / 96.0
        if ([Math]::Abs($window.width - 120 * $scale) -gt 1) { throw 'Standby entry did not shrink' }
        AssertFocus
        Screenshot 'standby'
    }
    Test 'Preferences persist pin and manual position independently of ASR settings' {
        $family = $AppUserModelId.Split('!')[0]
        $paths = @(
            (Join-Path $env:LOCALAPPDATA "VoxType/development-profiles/$ProfileId/overlay.json"),
            (Join-Path $env:LOCALAPPDATA "Packages/$family/LocalCache/Local/VoxType/development-profiles/$ProfileId/overlay.json")
        )
        $path = $paths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $path) { throw 'Preferences file was not saved' }
        $preferences = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if (-not $preferences.Pinned -or -not $preferences.Anchor -or -not $preferences.Anchor.Monitor) { throw 'Pin or drag position not saved' }
    }
    Test 'Failure stays visible and retry starts a microphone-free fixture' {
        Preview 'failed'
        UI @('wait-for', 'RetryRecording', '-t', '2000')
        Screenshot 'failure'
        UI @('invoke', 'RetryRecording')
        UI @('wait-for', 'OverlayStatus', '--value', '开始说话…', '-t', '2000')
        UI @('wait-for', 'StopRecording', '-t', '2000')
        AssertFocus
    }
    Test 'Idle entry starts explicitly and microphone stays off until clicked' {
        Preview 'hide'
        UI @('wait-for', 'StartRecording', '-t', '2000')
        UI @('wait-for', 'StopRecording', '--gone', '-t', '1000')
        UI @('invoke', 'StartRecording')
        UI @('wait-for', 'OverlayStatus', '--value', '开始说话…', '-t', '2000')
        AssertFocus
    }
    Test 'Temporary mode hides automatically after completion' {
        UI @('invoke', 'OverlayMore')
        UI @('invoke', 'OverlayPin', '-w', "$( (Popup).hwnd )")
        Preview 'auto-completed'
        UI @('wait-for', 'OverlayStatus', '--gone', '-t', '3000')
        if (@(UI @('list-windows', '--json') | ConvertFrom-Json).Count -ne 0) { throw 'Temporary overlay stayed visible' }
        AssertFocus
    }
    Test 'Completion dismisses the open menu and recording bar' {
        Preview 'menu-completed'
        UI @('invoke', 'OverlayMore')
        Start-Sleep -Milliseconds 2800
        UI @('wait-for', 'OverlayPin', '--gone', '-t', '1000')
        UI @('wait-for', 'OverlayStatus', '--gone', '-t', '3000')
        AssertFocus
    }
    Test 'Light theme retains readable controls' {
        Activate '--check-overlay=voice --check-theme=light'
        UI @('wait-for', 'OverlayStatus', '--value', '松开结束', '-t', '2000')
        Screenshot 'light-listening'
        AssertFocus
    }
} finally {
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -ErrorAction SilentlyContinue }
    Activate '--check-overlay=hide'
    $failed = @($results | Where-Object status -eq 'FAIL').Count
    $unverified = @($results | Where-Object status -eq 'NOT_VERIFIED').Count
    [ordered]@{ appPid=$AppPid; passed=$results.Count-$failed-$unverified; failed=$failed; unverified=$unverified; results=@($results.ToArray()) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
    $results | Format-Table -AutoSize
    Write-Host "Evidence: $artifacts"
}
if ($failed) { exit 1 }
