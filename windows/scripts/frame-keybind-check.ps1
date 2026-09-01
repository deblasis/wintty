#requires -Version 7
<#
    Frame chords, seam-actuated (issue #868): a chord pressed with focus on
    the frame -- the title bar, the tab strip, the chrome -- must reach the
    action it is bound to, and a key that belongs to whatever holds focus
    must never be taken from it.

    Actuation is the in-process test seam (WINTTY_TEST_SEAM=1, the named
    pipe). Zero OS input is synthesized: the 'focus' op moves real XAML
    focus and the 'chord' op calls the window's own frame-chord router --
    focus gate, residual table, libghostty match, dispatch -- with the
    modifier state passed in, because no key is actually held. What the
    seam cannot exercise is the framework hop above that router (WinUI
    raising KeyDown on the window content); everything below it is the
    shipped path.

    Scenarios, each against a FRESH app process:

      1. frame-layout-toggle:  focus a strip row, send Ctrl+Shift+, and the
         layout flips. The apprt-matched arm (the Windows-only residual
         table), and the acceptance case from the issue.
      2. frame-new-tab:        focus a strip row, send Ctrl+T and a tab is
         born. The libghostty-matched arm through a default keybind.
      3. frame-pin:            a user keybind for pin_tab fires from the
         frame and the active tab lands in the pinned prefix. pin_tab has
         no default chord anywhere in the Windows defaults, so the config
         binds one -- which is also the proof the match reads the LIVE
         keybind set, user overrides included.
      4. pane-owns-its-keys:   with focus in a pane the frame router stands
         down for every shape -- a bare letter, a bound chord, Ctrl+T --
         and the window is left untouched. This is the regression guard on
         the greedy-accelerator risk: a router that answered here would be
         a router that can eat what the terminal was typing. (The chords
         still work in a pane; they run through TerminalControl's own key
         path, which the seam does not drive.)
      5. frame-leaves-plain-keys: on the frame, an unmodified letter, the
         navigation keys the strip needs, and a modified-but-unbound chord
         are all refused and change nothing.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Virtual-key codes, named so the scenarios read as chords.
$VK = @{
    A = 0x41; K = 0x4B; P = 0x50; T = 0x54
    Comma = 0xBC; Enter = 0x0D; Space = 0x20; Right = 0x27; Escape = 0x1B
}

$Config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
window-theme = wintty
vertical-tabs-hover-expand = false
keybind = ctrl+shift+alt+p=pin_tab
keybind = space=new_tab
'@

$names = @('frame-1', 'frame-2', 'frame-3')
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$script:Scenarios = [System.Collections.Generic.List[object]]::new()

function Invoke-Focus($s, [string]$Target) {
    $r = Invoke-SeamCommand $s @{ op = 'focus'; target = $Target }
    if ($r.focus -ne $Target) {
        throw "PRODUCT_FAIL: asked for '$Target' focus, the router reads '$($r.focus)'"
    }
    return $r
}

function Invoke-Chord($s, [int]$Key, [bool]$Ctrl = $false, [bool]$Shift = $false,
                      [bool]$Alt = $false, [string]$Where = 'frame') {
    $r = Invoke-SeamCommand $s @{
        op = 'chord'; key = $Key; ctrl = $Ctrl; shift = $Shift; alt = $Alt
    }
    # The answer names the focus the ROUTER read, before any action it
    # dispatched re-homed focus of its own accord.
    if ($r.focus -ne $Where) {
        throw "PRODUCT_FAIL: the router read focus as '$($r.focus)', expected '$Where'"
    }
    return $r
}

function Assert-Dispatched($Resp, [string]$What) {
    if (-not $Resp.dispatched) {
        throw "PRODUCT_FAIL: $What was not dispatched from the frame"
    }
}

function Assert-Refused($Resp, [string]$What) {
    if ($Resp.dispatched) {
        throw "PRODUCT_FAIL: $What was dispatched, but nothing on the frame may claim it"
    }
}

function Invoke-Seed($s) {
    $seeded = Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = $names.Count; titles = $names }
    $got = @($seeded.state.tabs | ForEach-Object { $_.title })
    if (($got -join ',') -ne ($names -join ',')) {
        throw "PRODUCT_FAIL: seed order is [$($got -join ',')], wanted [$($names -join ',')]"
    }
    return $seeded
}

function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
    $crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
    $s = $null
    $entry = [ordered]@{ name = $Name; ok = $false; class = ''; error = '' }
    Write-Host "=== scenario $Name ==="
    try {
        Assert-NoWintty -Context "The frame keybind scenario '$Name'"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $Config
        & $Body $s
        if ($s.Proc.HasExited) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $Name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host "PASS $Name" -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $Name [$($entry.class)]: $msg" -ForegroundColor Red
    } finally {
        if ($null -ne $s) { Stop-SeamSession $s }
    }
    if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
        $entry.ok = $false
        $entry.class = 'product'
        $entry.error = ($entry.error + ' crash.log grew during the scenario').Trim()
        Write-Host "FAIL $Name [product]: crash.log grew" -ForegroundColor Red
    }
    $script:Scenarios.Add($entry)
}

# ---- scenarios -------------------------------------------------------------

Invoke-Scenario 'frame-layout-toggle' {
    param($s)
    $seeded = Invoke-Seed $s
    if (-not $seeded.state.vertical) { throw 'HARVEST_MISS: the session did not start vertical' }
    [void](Invoke-Focus $s 'frame')
    $r = Invoke-Chord $s $VK.Comma -Ctrl $true -Shift $true
    Assert-Dispatched $r 'Ctrl+Shift+, (toggle tab layout)'
    if ($r.state.vertical) {
        throw 'PRODUCT_FAIL: the chord was dispatched from the frame but the layout is still vertical'
    }
    if ($r.state.switching) { throw 'PRODUCT_FAIL: the layout is still switching at ack' }
}

Invoke-Scenario 'frame-new-tab' {
    param($s)
    $seeded = Invoke-Seed $s
    $before = $seeded.state.tabs.Count
    [void](Invoke-Focus $s 'frame')
    $r = Invoke-Chord $s $VK.T -Ctrl $true
    Assert-Dispatched $r 'Ctrl+T (new tab)'
    if ($r.state.tabs.Count -ne $before + 1) {
        throw "PRODUCT_FAIL: Ctrl+T from the frame left $($r.state.tabs.Count) tabs, wanted $($before + 1)"
    }
}

Invoke-Scenario 'frame-pin' {
    param($s)
    [void](Invoke-Seed $s)
    # Pin acts on the ACTIVE tab, so name it first through the manager.
    [void](Invoke-SeamCommand $s @{ op = 'select'; index = 2 })
    [void](Invoke-Focus $s 'frame')
    $r = Invoke-Chord $s $VK.P -Ctrl $true -Shift $true -Alt $true
    Assert-Dispatched $r 'Ctrl+Shift+Alt+P (pin tab)'
    if (-not $r.state.tabs[0].pinned -or $r.state.tabs[0].title -ne 'frame-3') {
        $order = ($r.state.tabs | ForEach-Object { "$($_.title)$(if ($_.pinned) { '*' })" }) -join ','
        throw "PRODUCT_FAIL: the pin chord left the strip as [$order]; frame-3 should head it, pinned"
    }
}

Invoke-Scenario 'pane-owns-its-keys' {
    param($s)
    $seeded = Invoke-Seed $s
    $before = $seeded.state.tabs.Count
    [void](Invoke-Focus $s 'pane')
    # A bare letter is what the terminal is typing; a bound chord and a
    # default-bound chord are what the pane's own key path already
    # handles. None of the three may be answered from here.
    Assert-Refused (Invoke-Chord $s $VK.A -Where 'pane') 'a bare letter with the pane focused'
    Assert-Refused (Invoke-Chord $s $VK.Comma -Ctrl $true -Shift $true -Where 'pane') `
        'Ctrl+Shift+, with the pane focused'
    $r = Invoke-Chord $s $VK.T -Ctrl $true -Where 'pane'
    Assert-Refused $r 'Ctrl+T with the pane focused'
    if (-not $r.state.vertical) { throw 'PRODUCT_FAIL: the layout changed with the pane focused' }
    if ($r.state.tabs.Count -ne $before) {
        throw "PRODUCT_FAIL: the tab count moved to $($r.state.tabs.Count) with the pane focused"
    }
}

Invoke-Scenario 'frame-leaves-plain-keys' {
    param($s)
    $seeded = Invoke-Seed $s
    $before = $seeded.state.tabs.Count
    [void](Invoke-Focus $s 'frame')
    Assert-Refused (Invoke-Chord $s $VK.A) 'a bare letter on the frame'
    Assert-Refused (Invoke-Chord $s $VK.Escape) 'Escape on the frame'
    Assert-Refused (Invoke-Chord $s $VK.Enter) 'Enter on the frame'
    Assert-Refused (Invoke-Chord $s $VK.Right) 'the Right arrow on the frame'
    # Space carries this scenario. The config above binds it to new_tab on
    # purpose: an unmodified key that IS bound, to something plainly
    # visible, so the shape rule has to be what refuses it -- the strip
    # activates its focused row with Space and must keep it.
    Assert-Refused (Invoke-Chord $s $VK.Space) 'a bound bare Space on the frame'
    Assert-Refused (Invoke-Chord $s $VK.K -Ctrl $true -Shift $true) 'an unbound Ctrl+Shift+K'
    $r = Invoke-Chord $s $VK.A -Shift $true
    Assert-Refused $r 'Shift+A on the frame'
    if (-not $r.state.vertical) { throw 'PRODUCT_FAIL: a refused key still changed the layout' }
    if ($r.state.tabs.Count -ne $before) {
        throw "PRODUCT_FAIL: a refused key still left $($r.state.tabs.Count) tabs"
    }
}

# ---- verdict ---------------------------------------------------------------

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=1); zero synthesized OS input'
    scenarios = $script:Scenarios
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'scenario                      verdict'
Write-Host '----------------------------  -------'
foreach ($sc in $script:Scenarios) {
    $verdict = if ($sc.ok) { 'PASS' } else { "FAIL ($($sc.class))" }
    Write-Host ("{0,-29} {1}" -f $sc.name, $verdict)
}

$product = @($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'product' })
$harness = @($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'harness' })
if ($product.Count -gt 0) { exit 2 }
if ($harness.Count -gt 0) { exit 1 }
exit 0
