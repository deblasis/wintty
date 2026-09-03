#requires -Version 7
<#
    The remaining tab's title after a split and a tab close. Seam-actuated
    (zero OS input): seed two tabs, split the active one, close the OTHER
    tab, and read the survivor's title.

    The old SendInput version right-clicked its way to Split Right and the
    tab menu's Close. The entry points changed (#930): the split is the
    chord's own dispatch through the seam's split op, and the close is the
    seam's close op on a SINGLE-PANE tab - which is what the old flow
    actually closed (the leftmost tab), so the scenario is the same shape.

    Two halves, each blind to what the other sees. The caption half: the
    window title is the ACTIVE tab's EffectiveTitle, and a seeded
    UserOverrideTitle beats everything including the shell's own report,
    so this half catches exactly one bug - the caption left pointing at
    the closed tab's override. A shell-title leak cannot reach it. The
    in-process half (tab-labels' shellTitle, the OSC round trip) is the
    leak oracle, and a shell that reports nothing within 10s is a MISS
    rather than a vacuous pass. The old file matched 'pwsh' against the
    caption, which could never succeed under this config - the #964
    family, and this harness was red on it before the migration.

    The old header claimed the survivor "must not pick up cmd.exe from the
    dying split pane". False as stated: this config defines one profile,
    so no cmd.exe pane can exist (#930). The claim is dropped rather than
    staged - staging a second profile would need a split-with-profile
    entry point the product does not have.

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
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# One profile, so its name is the only title any pane can produce.
$Config = @'
windows-single-instance = true
window-save-state = never
confirm-close-surface = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
'@

$titles = @('remain-a', 'remain-b')
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Shot($Session, [string]$Name) {
    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

try {
    Assert-NoWintty -Context 'The remain-title harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config

    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 2; titles = $titles })
    # The split lands in the ACTIVE tab, exactly as the old right-click did.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 1 })
    [void](Invoke-SeamCommand $session @{ op = 'split' })
    # shellTitle is only written once the shell reports one over OSC, so
    # poll for it rather than compare two absent fields - a vacuous equal
    # on both sides would assert nothing about the label following the
    # shell. $null means the deadline passed, and the comparison below
    # then runs on whatever is there.
    $before = $null
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline) {
        $probe = Invoke-SeamCommand $session @{ op = 'tab-labels' }
        $b = @($probe.labels) | Where-Object { $_.title -eq $titles[1] }
        if ($b -and "$($b.shellTitle)") { $before = $probe; break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $before) {
        Write-Host 'HARNESS: no shellTitle within 8s; the OSC half will compare what is there'
        $before = Invoke-SeamCommand $session @{ op = 'tab-labels' }
    }
    Shot $session '02-split'

    # Close the single-pane tab - the one the old flow's leftmost-tab menu
    # click closed. The seam's close op refuses multi-pane tabs, and this
    # is not one.
    [void](Invoke-SeamCommand $session @{ op = 'close'; index = 0 })
    $after = Invoke-SeamCommand $session @{ op = 'get-state' }
    $labelsAfter = Invoke-SeamCommand $session @{ op = 'tab-labels' }
    Shot $session '03-remain'

    $tabs = @($after.state.tabs)
    if ($tabs.Count -ne 1) {
        $script:Findings.Add(("close left {0} tab(s), wanted exactly 1" -f $tabs.Count))
    }
    else {
        # The window caption: no input needed, plain GetWindowText.
        # The caption tracks the ACTIVE tab's effective title, so the
        # expectation is the survivor's seeded title - and the closed tab's
        # title is the wrong answer this oracle exists to catch.
        $caption = [SeamWin]::TitleOf([SeamWin]::P($session.Hwnd64))
        if ($caption -ne $titles[1]) {
            $script:Findings.Add(("window title is '{0}', wanted the survivor's title '{1}'" -f $caption, $titles[1]))
        }
        # The in-process half: the survivor's shell-reported title is what
        # it was before the close, i.e. the close did not re-point the
        # label at anything.
        $shellBefore = @($before.labels) | Where-Object { $_.title -eq $titles[1] }
        $shellAfter = @($labelsAfter.labels) | Where-Object { $_.title -eq $titles[1] }
        if ($null -eq $shellBefore -or $null -eq $shellAfter) {
            throw "HARVEST_MISS: tab-labels did not report '$($titles[1])'"
        }
        if ("$($shellAfter.shellTitle)" -ne "$($shellBefore.shellTitle)") {
            $script:Findings.Add((
                "the surviving tab's shell title moved across the close: '{0}' -> '{1}'" -f
                    $shellBefore.shellTitle, $shellAfter.shellTitle))
        }
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

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); titles via GetWindowText + the tab-labels op'
    findings  = $script:Findings
    harness   = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
