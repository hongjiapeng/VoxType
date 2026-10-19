param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$AppUserModelId)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/polish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$results = [Collections.Generic.List[object]]::new()
Add-Type -AssemblyName System.Drawing
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-overlay-interaction.ps1') -Raw
$match = [regex]::Match($source, "(?s)Add-Type @'\r?\n(.*?)\r?\n'@")
Add-Type $match.Groups[1].Value
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OverlayChromeProbe {
 [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string type,string name);
 [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window,IntPtr region);
 [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int a,int b,int c,int d);
 [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr region);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window,int command);
 public static IntPtr Bridge(IntPtr window) => FindWindowEx(window,IntPtr.Zero,"Microsoft.UI.Content.DesktopChildSiteBridge",null);
 public static bool NoBinaryClip(IntPtr window) { var region=CreateRectRgn(0,0,0,0); try{return GetWindowRgn(window,region)==0;}finally{DeleteObject(region);} }
}
'@
function UI([string[]]$Arguments) {
    $output = @(& winapp ui @Arguments -a $AppPid)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($output -join "`n")" }
    $output
}
function Activate([string]$Arguments) { [OverlayInteractionProbe]::Activate($AppUserModelId, $Arguments) | Out-Null }
function Windows { @(UI @('list-windows', '--json') | ConvertFrom-Json) }
function Overlay { $window = Windows | Where-Object title -eq 'VoxType 语音输入' | Select-Object -First 1; if (-not $window) { throw 'Overlay missing' }; $window }
function Menu {
    foreach ($popup in (Windows | Where-Object title -eq 'PopupHost')) {
        $tree = UI @('inspect', '-w', "$($popup.hwnd)", '--json') | ConvertFrom-Json
        if (($tree | ConvertTo-Json -Depth 30) -match '"automationId":\s*"OverlayPin"') { return $popup }
    }
    throw 'Options menu missing'
}
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body | Out-Null; $results.Add([pscustomobject]@{ name=$Name; status='PASS' }) }
    catch { $results.Add([pscustomobject]@{ name=$Name; status='FAIL'; detail="$_" }) }
}
function Capture([string]$Name) {
    $path = Join-Path $artifacts "$Name.png"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        UI @('screenshot', '-w', "$( (Overlay).hwnd )", '-o', $path) | Out-Null
        $bitmap = [Drawing.Bitmap]::new($path)
        try { $painted = $bitmap.GetPixel([int]($bitmap.Width/2), [int]($bitmap.Height/2)).A -ge 250 }
        finally { $bitmap.Dispose() }
        if ($painted) { return $path }
        Start-Sleep -Milliseconds 250
    }
    throw "Capsule was not painted in screenshot $Name"
}
function Preview([string]$Theme) {
    Activate "--check-overlay=tray --check-theme=$Theme"
    UI @('wait-for', 'OverlayStatus', '--value', '开始说话…', '-t', '2000') | Out-Null
    Start-Sleep -Milliseconds 350
}
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
function SetPinned([bool]$Pinned) {
    if ((Diagnostic).OverlayPresentation.Pinned -eq $Pinned) { return }
    Preview 'dark'
    UI @('invoke', 'OverlayMore') | Out-Null
    UI @('invoke', 'OverlayPin', '-w', "$( (Menu).hwnd )") | Out-Null
}
function SelectTheme([string]$Theme, [string]$MainWindow) {
    UI @('invoke', 'ThemeSelector', '-w', $MainWindow) | Out-Null
    $found = UI @('search', $Theme, '--json') | ConvertFrom-Json
    $item = $found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Theme } | Select-Object -First 1
    if (-not $item) { throw "Theme option missing: $Theme" }
    UI @('invoke', $item.selector) | Out-Null
    $value = UI @('get-value', 'ThemeSelector', '-w', $MainWindow, '--json') | ConvertFrom-Json
    if ($value.text -ne $Theme) { throw "Theme selection did not change to $Theme" }
    Start-Sleep -Milliseconds 350
}
$initial = Diagnostic
if ($initial.IsBusy) { throw 'A real recording is active; leave it untouched.' }
try {
    Test 'Settings selector changes dark to light and back without distorting the capsule' {
        Activate '--settings'
        UI @('wait-for', 'NavGeneral', '-t', '2000') | Out-Null
        $main = Windows | Where-Object title -eq 'VoxType 设置' | Select-Object -First 1
        UI @('invoke', 'NavGeneral', '-w', "$($main.hwnd)") | Out-Null
        $originalTheme = (UI @('get-value', 'ThemeSelector', '-w', "$($main.hwnd)", '--json') | ConvertFrom-Json).text
        try {
            Activate '--check-overlay=failed-rejected --check-theme=dark'
            UI @('wait-for', 'OverlayStatus', '--value', '请求被拒绝', '-t', '2000') | Out-Null
            $dimensions = $null
            foreach ($theme in @('dark', 'light', 'dark')) {
                SelectTheme $theme "$($main.hwnd)"
                $p = (Diagnostic).OverlayPresentation
                if ($p.Theme -ne $theme -or [Math]::Abs($p.Radius-$p.Height/2) -gt 1) { throw 'Theme or capsule shape did not update through settings' }
                $size = "$($p.Width),$($p.Height)"
                if ($dimensions -and $size -ne $dimensions) { throw 'Changing theme changed capsule dimensions' }
                $dimensions = $size
                $path = Capture "settings-$theme"
                $bitmap = [Drawing.Bitmap]::new($path)
                try {
                    if ($bitmap.GetPixel(0,0).A -ne 0 -or $bitmap.GetPixel($bitmap.Width-1,0).A -ne 0) { throw 'Settings theme switch left a rectangular backing' }
                    $surface = $bitmap.GetPixel([int]($bitmap.Width/2), 8)
                    if ($theme -eq 'light' -and ($surface.R -gt 245 -or $surface.R -lt 210)) { throw 'Light surface did not use the soft gray palette' }
                    if ($theme -eq 'dark' -and $surface.R -gt 80) { throw 'Dark surface did not return after settings round trip' }
                } finally { $bitmap.Dispose() }
            }
        } finally {
            SelectTheme $originalTheme "$($main.hwnd)"
            [OverlayChromeProbe]::ShowWindow([IntPtr]$main.hwnd,0) | Out-Null
        }
    }

    Test 'Directional position menu is removed' {
        Preview 'dark'
        UI @('invoke', 'OverlayMore')
        $menu = Menu
        $tree = UI @('inspect', '-w', "$($menu.hwnd)", '--json') | ConvertFrom-Json
        if (($tree | ConvertTo-Json -Depth 30) -match 'OverlayMove|微调位置|向左|向右|向上|向下') { throw 'Directional commands still exist' }
        UI @('invoke', 'OverlayCancel', '-w', "$($menu.hwnd)")
    }
    Test 'Dark and light surfaces have transparent corners and smooth alpha edges' {
        foreach ($theme in @('dark', 'light')) {
            Preview $theme
            $path = Capture $theme
            $bitmap = [Drawing.Bitmap]::new($path)
            try {
                foreach ($point in @(@(0,0),@(($bitmap.Width-1),0),@(0,($bitmap.Height-1)),@(($bitmap.Width-1),($bitmap.Height-1)))) {
                    if ($bitmap.GetPixel($point[0],$point[1]).A -ne 0) { throw "$theme has an opaque corner/backplate" }
                }
                if ($bitmap.GetPixel([int]($bitmap.Width/2),[int]($bitmap.Height/2)).A -ne 255) { throw 'Surface is not opaque' }
                $partial = 0
                for ($x=0; $x -lt $bitmap.Height; $x++) {
                    for ($y=0; $y -lt $bitmap.Height; $y++) {
                        $alpha=$bitmap.GetPixel($x,$y).A
                        if ($alpha -gt 0 -and $alpha -lt 255) { $partial++ }
                    }
                }
                if ($partial -lt 10) { throw 'Rounded edge has no antialiased alpha coverage' }
            } finally { $bitmap.Dispose() }
        }
    }
    Test 'No GDI clipping remains and WinUI content fills the entire client' {
        $window = Overlay
        $handle = [IntPtr]$window.hwnd
        if (-not [OverlayChromeProbe]::NoBinaryClip($handle)) { throw 'Native HRGN still clips the edge' }
        $bridge = [OverlayChromeProbe]::Bridge($handle)
        if ($bridge -eq [IntPtr]::Zero) { throw 'WinUI content bridge missing' }
        $parent = [OverlayInteractionProbe]::Bounds($handle)
        $child = [OverlayInteractionProbe]::Bounds($bridge)
        if ($parent.Left -ne $child.Left -or $parent.Top -ne $child.Top -or $parent.Right -ne $child.Right -or $parent.Bottom -ne $child.Bottom) {
            throw 'WinUI child leaves an exposed native frame/inset'
        }
    }
    Test 'Light to dark theme round trip leaves no top line' {
        Preview 'dark'
        $before = Capture 'dark-before'
        Preview 'light'
        Preview 'dark'
        $after = Capture 'dark-after'
        $a=[Drawing.Bitmap]::new($before); $b=[Drawing.Bitmap]::new($after)
        try {
            for ($x=[int]($a.Width/4); $x -lt [int]($a.Width*3/4); $x++) {
                for ($y=0; $y -lt 4; $y++) {
                    $first=$a.GetPixel($x,$y); $last=$b.GetPixel($x,$y)
                    if ([Math]::Abs([int]$first.R-$last.R) -gt 2 -or [Math]::Abs([int]$first.G-$last.G) -gt 2 -or [Math]::Abs([int]$first.B-$last.B) -gt 2 -or [Math]::Abs([int]$first.A-$last.A) -gt 2) {
                        throw "Top edge changed after theme round trip at ($x,$y)"
                    }
                }
            }
        } finally { $a.Dispose(); $b.Dispose() }
    }
    Test 'Settings action opens and activates the main window after menu closes' {
        Preview 'dark'
        UI @('invoke', 'OverlayMore')
        UI @('invoke', 'OverlaySettings', '-w', "$( (Menu).hwnd )")
        Start-Sleep -Milliseconds 400
        $main = Windows | Where-Object title -eq 'VoxType 设置' | Select-Object -First 1
        if (-not $main) { throw 'Settings window did not open' }
        UI @('wait-for', 'NavGeneral', '-w', "$($main.hwnd)", '-t', '2000')
        if ([OverlayInteractionProbe]::GetForegroundWindow() -ne [IntPtr]$main.hwnd) { throw 'Settings window is visible but was not activated to foreground' }
        UI @('screenshot', '-w', "$($main.hwnd)", '-o', (Join-Path $artifacts 'settings.png'))
        [OverlayChromeProbe]::ShowWindow([IntPtr]$main.hwnd,6) | Out-Null
        UI @('invoke', 'OverlayMore', '-w', "$( (Overlay).hwnd )")
        UI @('invoke', 'OverlaySettings', '-w', "$( (Menu).hwnd )")
        Start-Sleep -Milliseconds 300
        if ([OverlayInteractionProbe]::GetForegroundWindow() -ne [IntPtr]$main.hwnd) { throw 'Minimized settings did not restore to foreground' }
        [OverlayChromeProbe]::ShowWindow([IntPtr]$main.hwnd,0) | Out-Null
    }
    Test 'Compact idle entry stays transparent in both themes' {
        SetPinned $true
        foreach ($theme in @('dark','light','dark','light','dark','light')) {
            Activate "--check-overlay=hide --check-theme=$theme"
            UI @('wait-for', 'StartRecording', '-w', "$( (Overlay).hwnd )", '-t', '2000')
            Start-Sleep -Milliseconds 250
            $path = Capture "idle-$theme"
            $bitmap=[Drawing.Bitmap]::new($path)
            try {
                if ($bitmap.GetPixel(0,0).A -ne 0 -or $bitmap.GetPixel($bitmap.Width-1,$bitmap.Height-1).A -ne 0) { throw 'Idle entry still has a rectangular backing' }
            } finally { $bitmap.Dispose() }
        }
    }
} finally {
    try {
        SetPinned $initial.OverlayPresentation.Pinned
        $restoreTheme = if ($initial.PreviewTheme) { $initial.PreviewTheme } else { $initial.Theme }
        Activate "--check-overlay=resume --check-theme=$restoreTheme"
    } catch { $results.Add([pscustomobject]@{ name='Restore initial overlay preferences'; status='FAIL'; detail="$_" }) }
    $failed=@($results | Where-Object status -eq 'FAIL').Count
    [ordered]@{ appPid=$AppPid; passed=$results.Count-$failed; failed=$failed; results=@($results.ToArray()) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
    $results | Format-Table -AutoSize
    Write-Host "Evidence: $artifacts"
}
if ($failed) { exit 1 }
