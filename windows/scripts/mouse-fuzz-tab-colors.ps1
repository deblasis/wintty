#requires -Version 7
<#
    Tab preset colours: every swatch plus None, active and inactive, a
    recolour pass, and a layout round trip each way.

    Seam-actuated: the harness synthesizes no OS input and never takes the
    foreground. Tabs come from seed-tabs, a colour from tab-color (the
    colour menu's own `tab.Color = color` assignment), selection from
    select, the layout from toggle-layout (the chord's router event) and the
    sidebar from toggle-sidebar. The right-click, the "Tab Color..." item
    and the swatch click are gone here; mouse-fuzz-remain.ps1 opens the tab
    menu and the picker through the seam's menu op and picks a swatch.

    Three oracles. The manager's colour per tab at every stop: what a tab was
    given survives selection, both layout switches and the recolour. The
    strip's ink pass, read through header-rect in the horizontal layout: every
    tagged tab gets ink and no None tab does. And the paint: in a capture of
    the horizontal strip, inactive tabs wearing different presets must be
    painted apart and tabs wearing the same one alike. In the vertical layout
    the strip must list every tab over UIA. Strip crops still go to shots/.

    Exits 0 clean, 2 findings, 1 could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @'
window-save-state = never
vertical-tabs = false
window-theme = wintty
theme = Catppuccin Mocha
'@

# Tab 0 stays default (None). Tabs 1..9 get every preset swatch.
$AllPresets = @('Blue', 'Purple', 'Pink', 'Red', 'Orange', 'Yellow', 'Green', 'Teal', 'Graphite')
$TabCount = 1 + $AllPresets.Count   # 10
$Labels = @('') + $AllPresets

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null
$result = [ordered]@{
    tabCount = $TabCount
    presets  = $AllPresets
    phases   = [System.Collections.Generic.List[object]]::new()
    recolors = [System.Collections.Generic.List[object]]::new()
}

function Shot-StripCrop($Session, [string]$Name, [int]$Width, [int]$Height) {
    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { return }
    $w = [Math]::Min($Width, $rc.W); $h = [Math]::Min($Height, $rc.Hh)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, [System.Drawing.Size]::new($w, $h))
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

# What the manager says each tab wears. The seam writes "color" only when a
# tab has one, so an absent field is None.
function Get-Colors($State) {
    return @($State.tabs | ForEach-Object { if ($_.color) { [string]$_.color } else { '' } })
}

function Assert-Colors($State, [string[]]$Want, [string]$Where) {
    $got = Get-Colors $State
    if ($got.Count -ne $Want.Count) {
        $script:Findings.Add("${Where}: $($got.Count) tabs, wanted $($Want.Count)")
        return
    }
    for ($i = 0; $i -lt $Want.Count; $i++) {
        if ($got[$i] -ne $Want[$i]) {
            $w = if ($Want[$i]) { $Want[$i] } else { 'None' }
            $g = if ($got[$i]) { $got[$i] } else { 'None' }
            $script:Findings.Add("${Where}: tab $i wears $g, wanted $w")
        }
    }
}

# The strip's side of the claim, horizontal only (header-rect reads the
# horizontal host). Two readings per tab, neither of them the model field the
# tab-color op wrote:
#   ink   - the foreground the strip's ink pass assigned the row: present on
#           every tagged tab, absent on every None tab;
#   paint - the row's dominant colour in a screen capture. Inactive tabs
#           wearing different presets must be painted apart, inactive tabs
#           wearing the same one alike, and None apart from every preset.
function Get-DominantColor($Bitmap, $Rc, $Rect) {
    $bins = @{}
    $x0 = [Math]::Max(0, $Rect.x - $Rc.L); $y0 = [Math]::Max(0, $Rect.y - $Rc.T)
    $x1 = [Math]::Min($Bitmap.Width, $x0 + $Rect.w); $y1 = [Math]::Min($Bitmap.Height, $y0 + $Rect.h)
    for ($y = $y0; $y -lt $y1; $y += 2) {
        for ($x = $x0; $x -lt $x1; $x += 2) {
            $p = $Bitmap.GetPixel($x, $y)
            $key = (($p.R -shr 3) -shl 10) -bor (($p.G -shr 3) -shl 5) -bor ($p.B -shr 3)
            if (-not $bins.ContainsKey($key)) { $bins[$key] = [System.Collections.Generic.List[object]]::new() }
            $bins[$key].Add($p)
        }
    }
    if ($bins.Count -eq 0) { return $null }
    $top = ($bins.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending | Select-Object -First 1).Value
    return @(
        [int](($top | Measure-Object -Property R -Average).Average),
        [int](($top | Measure-Object -Property G -Average).Average),
        [int](($top | Measure-Object -Property B -Average).Average))
}

function Get-Distance($A, $B) {
    return [Math]::Max([Math]::Abs($A[0] - $B[0]), [Math]::Max([Math]::Abs($A[1] - $B[1]), [Math]::Abs($A[2] - $B[2])))
}

function Assert-StripPaint($Session, [string[]]$Want, [string]$Where) {
    $state = (Invoke-SeamCommand $Session @{ op = 'get-state' }).state
    $active = [int]$state.active
    $rects = @{}
    for ($i = 0; $i -lt $Want.Count; $i++) {
        $r = Invoke-SeamCommand $Session @{ op = 'header-rect'; index = $i; part = 'row' }
        $hasInk = -not [string]::IsNullOrEmpty([string]$r.fg)
        $tagged = -not [string]::IsNullOrEmpty($Want[$i])
        if ($hasInk -ne $tagged) {
            $script:Findings.Add("${Where}: the strip's ink pass gives tab $i $(if ($hasInk) { "ink $($r.fg)" } else { 'no ink' }) while it wears $(if ($tagged) { $Want[$i] } else { 'None' })")
        }
        $rects[$i] = $r
    }

    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: lost the window rect before the paint check' }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    try {
        $paint = @{}
        for ($i = 0; $i -lt $Want.Count; $i++) {
            if ($i -eq $active) { continue }
            $c = Get-DominantColor $bmp $rc $rects[$i]
            if ($null -eq $c) { throw "HARVEST_MISS: tab $i's row has no pixels on screen" }
            $paint[$i] = $c
        }
    } finally { $bmp.Dispose() }

    $idx = @($paint.Keys | Sort-Object)
    for ($a = 0; $a -lt $idx.Count; $a++) {
        for ($b = $a + 1; $b -lt $idx.Count; $b++) {
            $i = $idx[$a]; $j = $idx[$b]
            $d = Get-Distance $paint[$i] $paint[$j]
            $ni = if ($Want[$i]) { $Want[$i] } else { 'None' }
            $nj = if ($Want[$j]) { $Want[$j] } else { 'None' }
            if ($ni -eq $nj -and $d -gt 12) {
                $script:Findings.Add("${Where}: tabs $i and $j both wear $ni but are painted $d apart")
            }
            elseif ($ni -ne $nj -and $d -lt 6) {
                $script:Findings.Add("${Where}: tab $i ($ni) and tab $j ($nj) are painted alike (distance $d)")
            }
        }
    }
}

# The vertical strip's own rows over UIA, as the original check read them.
# MUXC can virtualize a few, so a small shortfall is a warning, not a finding.
function Assert-VerticalItems([int64]$Hwnd64, [int]$Expected) {
    $best = 0
    $deadline = (Get-Date).AddSeconds(12)
    do {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($Hwnd64))
        $navCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
        $nav = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCond)
        $count = 0
        if ($null -ne $nav) {
            $itemCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
            $count = @($nav.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)).Count
        }
        if ($count -gt $best) { $best = $count }
        if ($count -ge $Expected) { return }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    if ($best -lt [Math]::Max(5, $Expected - 2)) {
        $script:Findings.Add("the vertical strip shows $best tab items, expected $Expected")
    } else {
        Write-Host "WARN vertical UIA items=$best expected=$Expected (virtualized)" -ForegroundColor Yellow
    }
}

function Assert-Layout($State, [bool]$Vertical, [string]$Where) {
    if ([bool]$State.vertical -ne $Vertical) {
        $want = if ($Vertical) { 'vertical' } else { 'horizontal' }
        throw "PRODUCT_FAIL: ${Where}: the layout is not $want"
    }
}

# Select each tab once; one strip crop per selection shows the active tab
# and all its inactive siblings. The colours are asserted at every stop.
function Invoke-ActiveCycle($Session, [bool]$Vertical, [string]$Prefix, [string[]]$Want) {
    $cropW = if ($Vertical) { 280 } else { 620 }
    $cropH = if ($Vertical) { 580 } else { 40 }
    for ($i = 0; $i -lt $Want.Count; $i++) {
        $r = Invoke-SeamCommand $Session @{ op = 'select'; index = $i }
        Assert-Layout $r.state $Vertical "$Prefix select $i"
        if ($r.state.active -ne $i) { $script:Findings.Add("$Prefix select ${i}: the active tab is $($r.state.active)") }
        Assert-Colors $r.state $Want "$Prefix with tab $i active"
        $tag = if ($Want[$i]) { $Want[$i] } else { 'None' }
        $state = if ($Vertical) { 'v' } else { 'h' }
        Shot-StripCrop $Session "$Prefix-$state-a$i-$tag" $cropW $cropH
    }
    if ($Vertical) { Assert-VerticalItems $Session.Hwnd64 $Want.Count }
    else { Assert-StripPaint $Session $Want $Prefix }
}

function Switch-Layout($Session, [bool]$ToVertical) {
    $r = Invoke-SeamCommand $Session @{ op = 'toggle-layout' }
    Assert-Layout $r.state $ToVertical 'after toggle-layout'
    if ($ToVertical -and $r.state.paneWidth -lt 120) {
        # A collapsed sidebar hides the colour bands a person checks the
        # crops for; the pane toggle is what the chevron dispatches.
        $r = Invoke-SeamCommand $Session @{ op = 'toggle-sidebar' }
    }
    return $r
}

function Add-Phase([string]$Name, [scriptblock]$Body) {
    $before = $script:Findings.Count
    & $Body
    $ok = $script:Findings.Count -eq $before
    $result.phases.Add([ordered]@{ name = $Name; ok = $ok })
    Write-Host ("{0} {1}" -f $(if ($ok) { 'OK  ' } else { 'FAIL' }), $Name)
}

try {
    Assert-NoWintty -Context 'The tab-color fuzz'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    [SeamWin]::PlaceOnTop($session.Hwnd64)
    Write-Host "hwnd=$($session.Hwnd64) pid=$($session.Proc.Id)"

    Add-Phase 'spawn-tabs' {
        $titles = @(0..($TabCount - 1) | ForEach-Object { "color-$_" })
        $r = Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = $TabCount; titles = $titles }
        if (@($r.state.tabs).Count -ne $TabCount) {
            throw "HARVEST_MISS: seed left $(@($r.state.tabs).Count) tabs, wanted $TabCount"
        }
        Assert-Layout $r.state $false 'after seeding'
        Assert-Colors $r.state (@('') * $TabCount) 'fresh tabs'
        Assert-StripPaint $session (@('') * $TabCount) 'fresh tabs'
        Shot-StripCrop $session 'h-initial-all-none' 620 40
    }

    Add-Phase 'assign-all-presets' {
        for ($i = 1; $i -lt $TabCount; $i++) {
            $r = Invoke-SeamCommand $session @{ op = 'tab-color'; index = $i; color = $AllPresets[$i - 1] }
        }
        Assert-Colors $r.state $Labels 'after assigning every preset'
        Assert-StripPaint $session $Labels 'after assigning every preset'
    }

    Add-Phase 'horiz-active-inactive-cycle' {
        Invoke-ActiveCycle $session $false 'horiz' $Labels
    }

    Add-Phase 'switch-to-vertical-preserve' {
        [void](Invoke-SeamCommand $session @{ op = 'select'; index = 3 })
        Shot-StripCrop $session 'h-before-switch-a3-Pink' 620 40
        $r = Switch-Layout $session $true
        Assert-Colors $r.state $Labels 'after switching to vertical'
    }

    Add-Phase 'vert-same-colors-as-horiz' {
        Invoke-ActiveCycle $session $true 'parity' $Labels
    }

    Add-Phase 'switch-back-horizontal-preserve' {
        $r = Switch-Layout $session $false
        Assert-Colors $r.state $Labels 'after switching back to horizontal'
        Invoke-ActiveCycle $session $false 'return' $Labels
    }

    $labelsAfter = @('', 'Teal', 'Orange', 'Pink', 'Pink', 'Orange', '', 'Green', 'Teal', 'Green')
    Add-Phase 'recolor-existing-tabs' {
        $changes = @(
            @{ i = 1; from = 'Blue';     to = 'Teal' },
            @{ i = 2; from = 'Purple';   to = 'Orange' },
            @{ i = 4; from = 'Red';      to = 'Pink' },
            @{ i = 6; from = 'Yellow';   to = 'None' },
            @{ i = 9; from = 'Graphite'; to = 'Green' }
        )
        foreach ($ch in $changes) {
            $r = Invoke-SeamCommand $session @{ op = 'tab-color'; index = $ch.i; color = $ch.to }
            $got = (Get-Colors $r.state)[$ch.i]
            $want = if ($ch.to -eq 'None') { '' } else { $ch.to }
            $result.recolors.Add([ordered]@{ tab = $ch.i; from = $ch.from; to = $ch.to; ok = ($got -eq $want) })
        }
        Invoke-ActiveCycle $session $false 'recolor' $labelsAfter
    }

    Add-Phase 'recolor-vert-parity' {
        [void](Switch-Layout $session $true)
        Invoke-ActiveCycle $session $true 'recolor' $labelsAfter
    }

    Add-Phase 'recolor-horiz-return' {
        [void](Switch-Layout $session $false)
        Invoke-ActiveCycle $session $false 'recolor-return' $labelsAfter
    }

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { $script:Findings.Add($msg) }
    else { $harnessError = $msg }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
    $script:Findings.Add('crash.log grew during the run')
}

$result.actuation = 'seam (WINTTY_TEST_SEAM=<session token>); no synthesized OS input'
$result.findings = $script:Findings
$result.harness = $harnessError
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
