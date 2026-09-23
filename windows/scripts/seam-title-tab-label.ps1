#requires -Version 7
<#
    Every tab shows its own active pane's title, live, seam-actuated end to
    end (wintty#1128, wintty#1129).

    Two scenarios against a fresh app process each, both on cmd, whose
    `title` builtin is the plainest way for a shell to set its console
    title (ConPTY forwards it as OSC 0):

      background  (#1129) Two tabs. Tab 0 is told to wait, then retitle
                  itself; tab 1 is selected before the wait ends. Tab 0's
                  label must follow while it sits in the background, and the
                  selected tab must not take its title.

      panes       (#1128) One tab split in two. Each pane titles itself; the
                  tab must name whichever pane is focused, follow focus both
                  ways, and ignore a title the unfocused pane sets.

    Each assertion reads the model label (EffectiveTitle), the raw shell
    title the tab holds, and the vertical strip's own rendered row text.

    Zero OS input is synthesized: what the shells run arrives as one
    ghostty_surface_text on the target pane, exactly the call committed IME
    text makes. The app runs with a private state tree, so it can run beside
    another Wintty.

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

$config = @"
windows-single-instance = false
window-save-state = never
vertical-tabs = true
vertical-tabs-hover-expand = false
default-profile = cmd
"@

$script:Scenarios = [System.Collections.Generic.List[object]]::new()

function Invoke-SeamCommandQuiet($s, [hashtable]$Command) {
    Send-SeamCommand $s $Command
    return Receive-SeamResponse $s $Command['op']
}

function Get-Labels($s) { return @((Invoke-SeamCommandQuiet $s @{ op = 'tab-labels' }).labels) }

# Poll until $Until holds for the label at $Index. The failure carries what
# was last seen, so a miss reads as a finding rather than a timeout.
function Wait-Title($s, [int]$Index, [scriptblock]$Until, [string]$What, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = (Get-Labels $s)[$Index]
        if (& $Until $last) { return $last }
        Start-Sleep -Milliseconds 300
    }
    throw ("PRODUCT_FAIL: {0}; tab {1} last read title='{2}' shellTitle='{3}' rendered='{4}'" -f
        $What, $Index, $last.title, $last.shellTitle, $last.rendered)
}

# The label, the raw title and the strip's row must all say $Want.
function Assert-Names($label, [string]$Want, [string]$What) {
    if ($label.title -ne $Want -or $label.shellTitle -ne $Want -or $label.rendered -ne $Want) {
        throw ("PRODUCT_FAIL: {0}: wanted '{1}', read title='{2}' shellTitle='{3}' rendered='{4}'" -f
            $What, $Want, $label.title, $label.shellTitle, $label.rendered)
    }
}

function New-Marker([string]$Tag) { return "wt-$Tag-$([guid]::NewGuid().ToString('N').Substring(0, 8))" }

# cmd's `ping -n N` waits about N-1 seconds; nothing else in a stock cmd
# waits without a console to read keys from.
function Get-DelayedTitle([string]$Marker, [int]$Seconds) {
    return "ping -n $($Seconds + 1) 127.0.0.1 >nul & title $Marker`r"
}

function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
    $entry = [ordered]@{ name = $Name; ok = $false; class = ''; error = ''; detail = '' }
    $s = $null
    Write-Host "=== scenario $Name ==="
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context "The title scenario '$Name'"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput -PrivateStateBase
        $entry.detail = & $Body $s
        if ($s.Proc.HasExited) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $Name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host ("PASS {0}: {1}" -f $Name, $entry.detail) -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $Name [$($entry.class)]: $msg" -ForegroundColor Red
    } finally {
        if ($null -ne $s) { Stop-SeamSession $s }
    }
    $script:Scenarios.Add([pscustomobject]$entry)
}

Invoke-Scenario 'background' {
    param($s)
    # Two real tabs with no override, so the label is the shell's to set.
    [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 2; titles = @('', '') })
    $first = New-Marker 'first'
    $second = New-Marker 'second'
    foreach ($i in 0, 1) {
        $m = if ($i -eq 0) { $first } else { $second }
        [void](Invoke-SeamCommand $s @{ op = 'select'; index = $i })
        [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = $i; text = "title $m`r" })
        Assert-Names (Wait-Title $s $i { param($l) $l.shellTitle -eq $m } "tab $i never took its own title '$m'") $m "tab $i, selected"
    }

    # Tab 0 retitles itself after tab 1 is selected again.
    $late = New-Marker 'late'
    [void](Invoke-SeamCommand $s @{ op = 'select'; index = 0 })
    [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = 0; text = (Get-DelayedTitle $late 4) })
    $state = (Invoke-SeamCommand $s @{ op = 'select'; index = 1 }).state
    if ($state.active -ne 1) { throw "HARNESS: tab 1 did not become active (active=$($state.active))" }

    $bg = Wait-Title $s 0 { param($l) $l.shellTitle -eq $late } `
        "the background tab did not follow its shell to '$late' (wintty#1129)"
    Assert-Names $bg $late 'background tab after its shell retitled'

    # Still in the background when it changed: the selection never moved.
    $labels = Get-Labels $s
    $active = (Invoke-SeamCommandQuiet $s @{ op = 'get-state' }).state.active
    if ($active -ne 1) { throw "HARNESS: the selection moved to tab $active during the wait" }
    Assert-Names $labels[1] $second 'the selected tab kept its own title (wintty#1128)'
    return "background tab followed to '$late'; selected tab kept '$second'"
}

Invoke-Scenario 'panes' {
    param($s)
    [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 1; titles = @('') })
    $a = New-Marker 'pane-a'
    [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = 0; text = "title $a`r" })
    Assert-Names (Wait-Title $s 0 { param($l) $l.shellTitle -eq $a } "the tab never took pane A's title") $a 'pane A focused'

    # The new pane is focused: the tab names it, not pane A.
    $state = (Invoke-SeamCommand $s @{ op = 'split'; orientation = 'vertical' }).state
    if ($state.tabs[0].leaves -ne 2) { throw "HARNESS: split did not make two leaves (leaves=$($state.tabs[0].leaves))" }
    $b = New-Marker 'pane-b'
    [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = 0; text = "title $b`r" })
    Assert-Names (Wait-Title $s 0 { param($l) $l.shellTitle -eq $b } "the tab did not follow the focused new pane to '$b'") $b 'pane B focused'

    # Focus follows both ways.
    $state = (Invoke-SeamCommand $s @{ op = 'focus-pane'; index = 0 }).state
    if ($state.tabs[0].activeLeaf -ne 0) { throw "HARNESS: focus-pane 0 left leaf $($state.tabs[0].activeLeaf) active" }
    Assert-Names (Wait-Title $s 0 { param($l) $l.shellTitle -eq $a } "focusing pane A did not bring back '$a'") $a 'pane A refocused'

    # Pane B retitles while unfocused: the tab keeps naming pane A.
    [void](Invoke-SeamCommand $s @{ op = 'focus-pane'; index = 1 })
    $b2 = New-Marker 'pane-b2'
    [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = 0; text = (Get-DelayedTitle $b2 3) })
    $state = (Invoke-SeamCommand $s @{ op = 'focus-pane'; index = 0 }).state
    if ($state.tabs[0].activeLeaf -ne 0) { throw "HARNESS: focus-pane 0 left leaf $($state.tabs[0].activeLeaf) active" }
    Start-Sleep -Seconds 6
    Assert-Names (Get-Labels $s)[0] $a "an unfocused pane's title stayed out of the tab"

    # And the tab picks it up the moment that pane is focused.
    [void](Invoke-SeamCommand $s @{ op = 'focus-pane'; index = 1 })
    Assert-Names (Wait-Title $s 0 { param($l) $l.shellTitle -eq $b2 } "focusing pane B did not bring its later title '$b2'") $b2 'pane B refocused'
    return "tab followed focus A '$a' / B '$b' / A, ignored B's unfocused '$b2', then showed it on focus"
}

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
    scenarios = $script:Scenarios
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if (@($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if (@($script:Scenarios | Where-Object { -not $_.ok }).Count -gt 0) { exit 1 }
exit 0
