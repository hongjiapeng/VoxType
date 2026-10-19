param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$AppUserModelId)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root ('ui-test-artifacts/session-handoff-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$source = Get-Content (Join-Path $PSScriptRoot 'test-overlay-interaction.ps1') -Raw
Add-Type ([regex]::Match($source, "(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
function Activate([string]$Arguments) { [OverlayInteractionProbe]::Activate($AppUserModelId, $Arguments) | Out-Null }
function Diagnostic {
    $path = Join-Path $artifacts ([guid]::NewGuid().ToString('N') + '.json')
    Activate "--check-diagnostics=$path"
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Diagnostics timed out' }
        Start-Sleep -Milliseconds 25
    }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function AssertListening([string]$Name) {
    $d = Diagnostic
    if (-not $d.OverlayVisible -or $d.OverlayPresentation.State -ne 'Listening' -or
        $d.OverlayPresentation.SessionId -ne 1 -or $d.OverlayPresentation.Revision -ne 2 -or
        $d.OverlayPresentation.IsPreview -ne $false -or $d.OverlayPresentation.ShowStop -or $d.OverlayPresentation.ShowMore) {
        throw "$Name failed: $($d.OverlayPresentation | ConvertTo-Json -Compress -Depth 4); visible=$($d.OverlayVisible)"
    }
    $output = & winapp ui wait-for OverlayStatus --value '松开结束' -a $AppPid -t 2000
    if ($LASTEXITCODE -ne 0) { throw "Listening UI missing: $output" }
}
function Listening {
    Activate '--check-session-update=1:1:Starting'
    Activate '--check-session-update=1:2:Listening'
}
$results = [Collections.Generic.List[object]]::new()
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{ name=$Name; status='PASS' }) }
    catch { $results.Add([pscustomobject]@{ name=$Name; status='FAIL'; detail="$_" }) }
}
$initial = Diagnostic
if ($initial.IsBusy) { throw 'Real recording active; leave it untouched' }
try {
    Test 'Shortcut session appears after leaving repeated layout previews' {
        1..5 | ForEach-Object { Activate '--check-overlay=listening'; Activate '--check-overlay=resume' }
        Listening
        AssertListening 'Preview to shortcut handoff'
    }
    Test 'Shortcut session replaces an interactive error fixture' {
        Activate '--check-overlay=failed'
        Listening
        AssertListening 'Interactive fixture handoff'
    }
    Test 'Delayed preview cannot replace a newer shortcut session' {
        Activate '--check-overlay=release'
        Listening
        Start-Sleep -Milliseconds 600
        AssertListening 'Delayed processing handoff'
    }
    Test 'Real session ordering still rejects late session and revision updates' {
        Activate '--check-overlay=resume'
        Listening
        Activate '--check-session-update=0:100:Completed'
        Activate '--check-session-update=1:1:Processing'
        AssertListening 'Stale production updates'
    }
} finally {
    Activate "--check-overlay=resume --check-theme=$($initial.PreviewTheme)"
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
    $results | Format-Table -AutoSize
    Write-Output "Artifacts: $artifacts"
}
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
