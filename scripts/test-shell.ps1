param([Parameter(Mandatory)][int]$AppPid, [ValidateSet('local')][string]$Target = 'local')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/VoxType.App/VoxType.App.csproj'
$artifacts = Join-Path $root ('ui-test-artifacts/' + $env:WINAPP_UI_WORKFLOW_ID)
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    $output
}
function UI([string[]]$Arguments) { Checked (@('ui') + $Arguments + @('--on', $Target, '-a', "$AppPid")) }
function Activate([string]$Arguments) { Checked @('run', $project, '--arch', 'x64', '--on', $Target, '--no-build', '--detach', '--json', '--args', $Arguments) | Out-Null }
function Walk($Nodes) { foreach ($node in $Nodes) { $node; Walk $node.children } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body | Out-Null; $results.Add([pscustomobject]@{ name=$Name; status='PASS' }) }
    catch {
        $status = if ("$_" -match 'No interactive desktop|no_interactive_desktop|No external foreground target') { 'NOT_VERIFIED' } else { 'FAIL' }
        $results.Add([pscustomobject]@{ name=$Name; status=$status; detail="$_" })
    }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VoxTypeShellProbe {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetStyle(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static void Click(int x,int y) {
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { SetCursorPos(x,y); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
        finally { SetThreadDpiAwarenessContext(old); }
    }
    public static uint ForegroundProcess(IntPtr window) { uint process; GetWindowThreadProcessId(window,out process); return process; }
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW")] static extern bool QuerySession(IntPtr server,uint session,int type,out IntPtr data,out uint bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr data);
    public static bool Unlocked() {
        IntPtr data;uint size;
        if(!QuerySession(IntPtr.Zero,0xffffffff,25,out data,out size)) return false;
        try { return size>=20 && Marshal.ReadInt32(data)==1 && Marshal.ReadInt32(data,12)==0 && Marshal.ReadInt32(data,16)==1; }
        finally { WTSFreeMemory(data); }
    }
}
'@
try {
    Test 'Startup hides both windows' { if (@(UI @('list-windows','--json') | ConvertFrom-Json).Count -ne 0) { throw 'Visible startup window' } }
    Test 'Duplicate activation opens same instance' {
        Activate '--settings'
        UI @('wait-for','NavGeneral','-t','3000')
        $apps = @(Get-Process 'VoxType.App')
        if ($apps.Count -ne 1 -or $apps[0].Id -ne $AppPid) { throw 'More than one instance or wrong instance' }
    }
    Test 'Platform services initialize without opening microphone' {
        UI @('wait-for','SettingsStatus','--value','语音输入已就绪','--contains','-t','2000')
    }
    Test 'Recognition page and local model fields' {
        UI @('invoke','NavRecognition')
        UI @('wait-for','ModelPath','-t','2000')
        UI @('screenshot','--output',(Join-Path $artifacts 'local-settings.png'))
    }
    Test 'Online provider drives same form' {
        UI @('invoke','ProviderSelector','--action','expand')
        $popup = @(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object title -eq 'PopupHost' | Select-Object -First 1
        if (-not $popup) { throw 'Dropdown window absent' }
        $tree = Checked @('ui','inspect','--interactive','--json','--on',$Target,'-w',"$($popup.hwnd)") | ConvertFrom-Json
        $option = @(Walk $tree.windows[0].elements) | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq 'OpenAI Compatible' } | Select-Object -First 1
        if (-not $option -or $option.width -le 0 -or $option.height -le 0) { throw 'Online option not present in live dropdown' }
        Checked @('ui','invoke',$option.selector,'--action','select','--on',$Target,'-w',"$($popup.hwnd)")
        UI @('wait-for','BaseUrl','-t','2000')
        UI @('wait-for','ApiKey','-t','2000')
        UI @('wait-for','OnlineModel','-t','2000')
        UI @('screenshot','--output',(Join-Path $artifacts 'online-settings.png'))
    }
    Test 'Text input updates VM before lost focus' {
        UI @('set-value','BaseUrl','http://example.com/v1')
        UI @('invoke','SaveSettings')
        UI @('wait-for','SettingsStatus','--value','Base URL 需要 HTTPS','--contains','-t','2000')
        UI @('set-value','BaseUrl','https://api.openai.com/v1')
        UI @('set-value','OnlineModel','whisper-1')
        UI @('invoke','SaveSettings')
        UI @('wait-for','SettingsStatus','--value','设置已保存','--contains','-t','2000')
    }
    Test 'Hotkey validation before lost focus' {
        UI @('invoke','NavHotkeys')
        UI @('set-value','HotkeyKey','Escape')
        UI @('invoke','SaveSettings')
        UI @('wait-for','SettingsStatus','--value','主键支持','--contains','-t','2000')
        UI @('set-value','HotkeyKey','Space')
    }
    Test 'Closing settings keeps process resident' {
        UI @('invoke','Close')
        if (@(UI @('list-windows','--json') | ConvertFrom-Json).Count -ne 0) { throw 'Settings still visible' }
        if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { throw 'Process exited' }
    }
    Test 'Tray-style overlay remains outside switchers and foreground' {
        if (-not [VoxTypeShellProbe]::Unlocked()) { throw 'No interactive desktop: Windows reports the session locked or unavailable.' }
        $before = [VoxTypeShellProbe]::GetForegroundWindow()
        if ($before -eq [IntPtr]::Zero) { throw 'No interactive desktop: foreground HWND is zero.' }
        if (-not [VoxTypeShellProbe]::IsWindowVisible($before) -or [VoxTypeShellProbe]::ForegroundProcess($before) -eq $AppPid) { throw 'No external foreground target: focus validation needs a visible external input window.' }
        Activate '--check-overlay=tray'
        UI @('wait-for','StopRecording','-t','2000')
        if ([VoxTypeShellProbe]::GetForegroundWindow() -ne $before) { throw 'Overlay changed foreground on show' }
        $window = @(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object { $_.title -eq 'VoxType 语音输入' } | Select-Object -First 1
        if (-not $window) { throw 'Overlay absent' }
        $style = [VoxTypeShellProbe]::GetStyle([IntPtr]$window.hwnd,-20).ToInt64()
        if (($style -band 0x08000000) -eq 0 -or ($style -band 0x80) -eq 0 -or ($style -band 0x40000) -ne 0) { throw 'Incorrect window styles' }
        $inspection = UI @('inspect','StopRecording','--json') | ConvertFrom-Json
        $inspection | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $artifacts 'stop-control.json') -Encoding utf8
        $button = $inspection.windows[0].elements[0]
        [VoxTypeShellProbe]::Click([int]($button.x + $button.width/2),[int]($button.y + $button.height/2))
        if ([VoxTypeShellProbe]::GetForegroundWindow() -eq [IntPtr]::Zero) { throw 'No interactive desktop: foreground disappeared during physical click.' }
        UI @('wait-for','OverlayStatus','--value','结束按钮检查通过','-t','2000')
        if ([VoxTypeShellProbe]::GetForegroundWindow() -ne $before) { throw 'Stop click changed foreground' }
        UI @('screenshot','--output',(Join-Path $artifacts 'overlay-tray.png'))
    }
    Test 'Overlay hides without activation' {
        if (-not [VoxTypeShellProbe]::Unlocked()) { throw 'No interactive desktop: Windows reports the session locked or unavailable.' }
        $before = [VoxTypeShellProbe]::GetForegroundWindow()
        if ($before -eq [IntPtr]::Zero) { throw 'No interactive desktop: foreground HWND is zero.' }
        if (-not [VoxTypeShellProbe]::IsWindowVisible($before) -or [VoxTypeShellProbe]::ForegroundProcess($before) -eq $AppPid) { throw 'No external foreground target: focus validation needs a visible external input window.' }
        Activate '--check-overlay=hide'
        UI @('wait-for','OverlayStatus','--gone','-t','2000')
        if ([VoxTypeShellProbe]::GetForegroundWindow() -ne $before) { throw 'Overlay changed foreground on hide' }
    }
} finally {
    $results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $artifacts 'results.json') -Encoding utf8
    $results | Format-Table name,status,detail -Wrap
    "Evidence: $artifacts"
    try { Checked @('ui','yield','--on',$Target) | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow }
}
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
