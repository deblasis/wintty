<#
.SYNOPSIS
    The selected tab's fill reaches its header, including tabs whose header
    was built while another tab was selected.

.DESCRIPTION
    The horizontal strip paints the active tab through the TabViewItem
    template's Selected state: its setters show SelectedBackgroundPath and
    fill it from the per-item TabViewItemHeaderBackgroundSelected resource,
    and they put the selected stroke on the container. A setter takes its
    {ThemeResource} value when the item's template is applied and keeps it.
    So the per-item resource the strip writes is a claim, and the paint is
    whatever the setter resolved at that moment.

    The failure this exists to catch: a tab whose template was applied
    while it was NOT the selected one froze the strip-level fallback, which
    is Transparent by design, and every later selection of that tab showed
    a bare header with only the seam cover's line under it. seed-tabs makes
    that tab deterministically: it adds the tabs after the first in one
    dispatcher turn, so only the last one is selected when the strip lays
    them out, and the ones between are built unselected.

    Every check reads the live tree through the seam's selected-paint op,
    an observer that cannot re-apply a state and heal what it reads: the
    active header must be selected, its fill path shown and painted with
    the field brush (the one instance the seam cover also paints with), and
    its stroke the selected stroke. Zero synthesized OS input.

.NOTES
    Exit 0 clean, 2 product findings, 1 the harness could not run or
    checked nothing that matters.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$Config = @'
windows-single-instance = false
window-save-state = never
'@

# Built after the first and before the last, in one turn: the tabs whose
# header is laid out while another tab is the selected one.
$Count = 5
$BuiltBehind = 1..($Count - 2)
# Past the 167ms field settle, so the read is of the strip at rest.
$SettleMs = 300

$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Rows = [System.Collections.Generic.List[object]]::new()

function Test-ActiveHeader($Session, [int]$Index, [string]$Leg) {
    [void](Invoke-SeamCommand $Session @{ op = 'select'; index = $Index })
    Start-Sleep -Milliseconds $SettleMs
    $paint = Invoke-SeamCommand $Session @{ op = 'selected-paint' }
    $active = @($paint.headers | Where-Object { $_.active })
    if ($active.Count -ne 1) {
        throw "HARNESS: selected-paint reports $($active.Count) active headers after selecting tab $Index"
    }
    $h = $active[0]
    if ([int]$h.index -ne $Index) {
        throw "HARNESS: selected tab $Index but the active header is $($h.index)"
    }
    $why = @()
    if (-not $h.selected) { $why += 'the item is not IsSelected' }
    if (-not $h.pathShown) { $why += 'the Selected fill path is not shown' }
    if (-not $h.fillIsField) { $why += "the Selected fill path paints $($h.fill), not the field brush" }
    if (-not $h.borderIsExpected) { $why += "the container stroke is $($h.border), not the selected stroke" }
    $script:Rows.Add([ordered]@{
        leg = $Leg; index = $Index; builtBehind = ($BuiltBehind -contains $Index)
        fill = $h.fill; border = $h.border; ok = ($why.Count -eq 0)
    })
    if ($why.Count -gt 0) {
        $script:Findings.Add("$Leg/tab$Index$(if ($BuiltBehind -contains $Index) { ' (built unselected)' }): $($why -join '; ')")
    }
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

$crashMark = Get-SeamSessionMark
$session = $null
$harnessError = ''
try {
    Assert-NoWinttyFrom -ExePath $ExePath -Context 'The selected fill seam harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config

    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = $Count })
    Start-Sleep -Milliseconds 800

    # Every tab once forward and once back, so each built-behind tab is
    # selected from a neighbour on both sides and a second selection of the
    # same tab is checked too: a setter that froze stays frozen.
    foreach ($i in 0..($Count - 1)) { Test-ActiveHeader $session $i 'forward' }
    foreach ($i in ($Count - 1)..0) { Test-ActiveHeader $session $i 'back' }

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') {
        $script:Findings.Add($msg)
    } else {
        $harnessError = $msg
    }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-SeamCrashLogWritten -Since $crashMark)) {
    $script:Findings.Add('crash.log grew during the run')
}

@{ rows = $script:Rows; findings = $script:Findings } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'result.json')

$behind = @($script:Rows | Where-Object { $_.builtBehind }).Count
Write-Host "$($script:Rows.Count) selections checked, $behind of them on a tab built unselected"

# A run that never selected a built-behind tab cannot have seen the defect.
if ($behind -eq 0 -and $script:Findings.Count -eq 0) {
    Write-Host 'HARVEST_MISS: no tab built unselected was selected, so nothing here rules the defect out'
    exit 1
}

if ($script:Findings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($script:Findings.Count) finding(s):" -ForegroundColor Red
    foreach ($f in $script:Findings) { Write-Host "  $f" -ForegroundColor Red }
    exit 2
}
if ($harnessError) { exit 1 }

Write-Host ''
Write-Host 'every selected header paints the field and the selected stroke, built selected or not' -ForegroundColor Green
exit 0
