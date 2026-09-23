#requires -Version 7
<#
    A key a surface consumes to close itself never reaches a pane,
    seam-actuated, one scenario per close-on-key surface.

    On Windows a key press is a KeyDown plus a WM_CHAR, and the WM_CHAR is
    queued before the KeyDown runs. A surface that closes on Enter, Escape or
    Space in its KeyDown hands focus back to a pane, and the character
    follows: Enter's '\r' submits whatever is on that pane's prompt line. The
    fix raises one signal from every such surface before its close runs; the
    window arms every pane; a pane drops the next character only if it is
    the armed key's, and its next KeyDown retires the arm.

    Every scenario runs in a fresh app with the active tab split in two, so
    the pane the surface does NOT return focus to (the sibling) is under
    test too. It drives the surface's own act through the seam (the method
    its KeyDown, Click or ItemClick reaches) and asserts:

      a. every pane of the window is armed for exactly that key;
      b. the sibling pane drops that key's character (terminal-character
         runs the pane's real post-guard character decision);
      c. the arm is one-shot: the next identical character flows;
      d. the other pane's next KeyDown retires its arm (terminal-retire runs
         the same method OnKeyDown runs first), and the character after it
         flows.

    Two negative controls keep the positives honest: the search bar's close
    button clicked with no key down arms nothing, and a window where no
    surface closed has no arm anywhere.

    Boundary: the seam drives handler code one call below the framework. It
    cannot deliver a real WM_CHAR to a focus-reverted surface without
    synthesising OS input, which this repo does not do.

    Zero OS input is synthesized. Each app runs on a temp config and a
    private state base, and cleanup stops only the processes this run
    started from -ExePath.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # Run only the scenarios whose name matches this wildcard.
    [string]$Only = '*',
    # A script dot-sourced after the built-in scenarios, for surfaces a
    # build adds on top of this tree. It calls Invoke-Scenario like the
    # scenarios below do.
    [string]$ExtraScenarios = '',
    # Config lines appended to the staged config, for keys a build adds.
    [string[]]$ConfigExtra = @()
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: missing exe: $ExePath"
    exit 1
}

$config = @"
windows-single-instance = false
window-save-state = never
command = cmd.exe
"@
if ($ConfigExtra.Count -gt 0) { $config = $config + "`n" + ($ConfigExtra -join "`n") }

$Chars = @{ Return = "`r"; Escape = [string][char]0x1B; Space = ' ' }

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Name, [bool]$Ok, [string]$Class, [string]$Detail) {
    $script:Results.Add([pscustomobject]@{ name = $Name; ok = $Ok; class = $Class; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name [$Class] $Detail" -ForegroundColor Red }
}

function Seam($s, [hashtable]$Command) { Invoke-SeamCommand $s $Command }

function Arms($s) { (Seam $s @{ op = 'consumed-close-arms' }).panes }

# One fresh app, the active tab split so it holds two panes, then $Setup
# (layout, tabs, an open surface), then $Drive (the close), then the checks.
function Invoke-Scenario {
    param(
        [Parameter(Mandatory)][string]$Name,
        # Return, Escape or Space: the key the close consumed. 'None' for a
        # negative control, which asserts nothing is armed anywhere.
        [Parameter(Mandatory)][string]$Key,
        [scriptblock]$Setup = {},
        [Parameter(Mandatory)][scriptblock]$Drive
    )
    if ($Name -notlike $Only) { return }
    Write-Host "== $Name" -ForegroundColor Cyan
    $s = $null
    try {
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput -PrivateStateBase
        [void](Seam $s @{ op = 'split'; orientation = 'vertical' })
        & $Setup $s

        # Nothing is armed before the close: a leftover arm would make every
        # assertion below pass for the wrong reason.
        $stale = @(Arms $s | Where-Object { $_.armed -ne 'None' })
        if ($stale.Count -gt 0) {
            Add-Result $Name $false 'harness' "armed before the close: $(($stale | ConvertTo-Json -Compress))"
            return
        }

        & $Drive $s
        $panes = @(Arms $s)
        if ($panes.Count -lt 2) {
            Add-Result $Name $false 'harness' "expected at least two panes, found $($panes.Count)"
            return
        }

        if ($Key -eq 'None') {
            $armed = @($panes | Where-Object { $_.armed -ne 'None' })
            Add-Result $Name ($armed.Count -eq 0) 'product' ("arms: " + (($panes | ForEach-Object { "$($_.tab)/$($_.leaf)=$($_.armed)" }) -join ' '))
            return
        }

        # a. every pane, in every tab, armed for exactly this key.
        $wrong = @($panes | Where-Object { $_.armed -ne $Key })
        Add-Result "$Name/every-pane-armed" ($wrong.Count -eq 0) 'product' `
            ("expected $Key; " + (($panes | ForEach-Object { "$($_.tab)/$($_.leaf)=$($_.armed)" }) -join ' '))

        # The split tab, wherever the close and the setup moved it (pinning
        # reorders): leaf 0 is the pane the split left behind, the sibling;
        # leaf 1 the other.
        $active = [int]@($panes | Group-Object tab | Where-Object { $_.Count -ge 2 })[0].Name
        $ch = $Chars[$Key]

        # b. the sibling drops the key's character.
        $first = Seam $s @{ op = 'terminal-character'; tab = $active; leaf = 0; char = $ch }
        Add-Result "$Name/sibling-drops-it" ([bool]$first.suppressed) 'product' "armedBefore=$($first.armedBefore) suppressed=$($first.suppressed)"

        # c. one-shot: the next identical character is the user's.
        $second = Seam $s @{ op = 'terminal-character'; tab = $active; leaf = 0; char = $ch }
        Add-Result "$Name/one-shot" (-not $second.suppressed -and $second.armedBefore -eq 'None') 'product' "armedBefore=$($second.armedBefore) suppressed=$($second.suppressed)"

        # d. the other pane's next KeyDown retires its arm, and the character
        # after that KeyDown flows.
        $retire = Seam $s @{ op = 'terminal-retire'; tab = $active; leaf = 1 }
        $after = Seam $s @{ op = 'terminal-character'; tab = $active; leaf = 1; char = $ch }
        Add-Result "$Name/keydown-retires" ([bool]$retire.wasArmed -and -not $after.suppressed) 'product' "wasArmed=$($retire.wasArmed) then suppressed=$($after.suppressed)"
    }
    catch {
        $msg = $_.Exception.Message
        $class = if ($msg -like 'PRODUCT_*') { 'product' } else { 'harness' }
        Add-Result $Name $false $class $msg
    }
    finally {
        if ($s) { Stop-SeamSession $s }
    }
}

$openPalette = { param($s) [void](Seam $s @{ op = 'palette-open' }) }
# seed-tabs keeps the first tab, the split one, and grows a second.
$twoTabs = { param($s) [void](Seam $s @{ op = 'seed-tabs'; count = 2 }); [void](Seam $s @{ op = 'select'; index = 0 }) }

# -- The negative controls ---------------------------------------------------

Invoke-Scenario 'control-no-close' 'None' -Drive { param($s) }
Invoke-Scenario 'control-search-button-mouse' 'None' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'search' })
}

# -- Command palette ----------------------------------------------------------

Invoke-Scenario 'palette-escape' 'Escape' -Setup $openPalette -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'palette'; key = 'escape' })
}
Invoke-Scenario 'palette-enter' 'Return' -Setup {
    param($s)
    [void](Seam $s @{ op = 'palette-open' })
    # A needle nothing matches: Enter then runs no command, and the signal
    # is still raised, because it comes before the act.
    [void](Seam $s @{ op = 'palette-type'; text = 'zzqx-no-such-command'; settle = $false })
} -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'palette'; key = 'enter' })
}
Invoke-Scenario 'palette-row-space' 'Space' -Setup {
    param($s)
    [void](Seam $s @{ op = 'palette-open' })
    [void](Seam $s @{ op = 'palette-type'; text = 'zzqx-no-such-command'; settle = $false })
} -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'palette-item'; held = 'space' })
}

# -- In-pane search bar -------------------------------------------------------

Invoke-Scenario 'search-escape' 'Escape' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'search'; key = 'escape' })
}
Invoke-Scenario 'search-close-button-enter' 'Return' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'search'; held = 'return' })
}

# -- Tab overview -------------------------------------------------------------

Invoke-Scenario 'overview-escape' 'Escape' -Setup $twoTabs -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'overview'; key = 'escape' })
}
Invoke-Scenario 'overview-enter' 'Return' -Setup $twoTabs -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'overview'; key = 'enter' })
}
Invoke-Scenario 'overview-tile-space' 'Space' -Setup $twoTabs -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'overview'; held = 'space' })
}

# -- Tab strips ---------------------------------------------------------------

Invoke-Scenario 'strip-row-space' 'Space' -Setup $twoTabs -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'strip-select'; index = 1; held = 'space' })
}
Invoke-Scenario 'vertical-row-enter' 'Return' -Setup {
    param($s)
    & $twoTabs $s
    [void](Seam $s @{ op = 'toggle-layout' })
} -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'strip-select'; index = 1; held = 'return' })
}
Invoke-Scenario 'vertical-pinned-row-enter' 'Return' -Setup {
    param($s)
    & $twoTabs $s
    [void](Seam $s @{ op = 'pin'; index = 1 })
    [void](Seam $s @{ op = 'toggle-layout' })
} -Drive {
    param($s)
    # Pinning moves the tab into the pinned prefix; find it by its flag.
    $state = (Seam $s @{ op = 'get-state' }).state
    $pinned = @($state.tabs | Where-Object { $_.pinned })[0].index
    [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'shelf'; index = $pinned; key = 'enter' })
}

# -- Notices ------------------------------------------------------------------

Invoke-Scenario 'notice-enter' 'Return' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'notice'; key = 'enter' })
}
Invoke-Scenario 'notice-button-space' 'Space' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'notice'; held = 'space' })
}

# -- Framework dialogs and flyouts --------------------------------------------

Invoke-Scenario 'rename-dialog-enter' 'Return' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'rename-dialog'; held = 'return' })
}
Invoke-Scenario 'rename-dialog-escape' 'Escape' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'rename-dialog'; held = 'escape' })
}
Invoke-Scenario 'pane-menu-escape' 'Escape' -Drive {
    param($s) [void](Seam $s @{ op = 'consumed-close-drive'; surface = 'pane-menu'; held = 'escape' })
}

if ($ExtraScenarios) {
    if (-not (Test-Path $ExtraScenarios)) { Write-Host "HARNESS: missing extra scenarios: $ExtraScenarios"; exit 1 }
    . $ExtraScenarios
}

# -- Report ---------------------------------------------------------------------

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>, WINTTY_TEST_SEAM_INPUT=1); zero synthesized OS input'
    scenarios = $script:Results
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Results.Count -eq 0) {
    Write-Host "HARNESS: -Only '$Only' selected no scenario"
    exit 1
}

Write-Host ''
foreach ($r in $script:Results) {
    Write-Host ("{0,-44} {1}" -f $r.name, $(if ($r.ok) { 'PASS' } else { "FAIL ($($r.class))" }))
}

if (@($script:Results | Where-Object { -not $_.ok -and $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if (@($script:Results | Where-Object { -not $_.ok }).Count -gt 0) { exit 1 }
exit 0
