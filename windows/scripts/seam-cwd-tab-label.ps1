#requires -Version 7
<#
    The tab label at a shell prompt, seam-actuated end to end (issue #873).

    A native Windows shell has two ways to say where it is, and the two are
    nothing alike. cmd has only `PROMPT $p`, a RAW Windows path carried on
    OSC 9;9; a native PowerShell session sends a `file://HOST/c:/dir` URL on
    OSC 7. One scenario each, both against a fresh app process, and each
    asks two independent questions:

      (i)  did the report reach the app at all -- the seam reads the pane's
           own LastCwd, which stays null for a native shell whenever either
           arm is dead;
      (ii) does the strip actually SAY that folder -- read twice, from the
           row's own TextBlock through the seam and from the rendered row's
           UIA Name, neither of which is the model property under test.

    Each also asserts the interpreter icon: the nav item must wear a decoded
    image (not an empty ImageIcon), and a final check says pwsh's must not be
    cmd's.

    A third scenario asks the security question the first two cannot: a
    reported cwd is a spawn directory, so a UNC one names a server Windows
    authenticates to. It injects the raw OSC 9;9 form directly -- a local
    path and a remote one, in that order -- and requires the local report to
    be the one still standing.

    Zero OS input is synthesized: what the shells run arrives as one
    ghostty_surface_text on the focused pane, exactly the call committed IME
    text makes. The machine stays usable for the whole run.

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

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# One coordinate space for every rect this harness reads (-4 =
# PER_MONITOR_AWARE_V2).
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

$script:MainHwnd64 = 0

# The rendered row names, straight off the strip's automation tree. This is
# the second oracle: it never touches TabModel and never touches the seam's
# own readout, so a label that is right in the model and wrong on screen
# still fails here.
function Get-UiaRowNames {
    $root = $UIA::FromHandle([SeamWin]::P($script:MainHwnd64))
    if ($null -eq $root) { throw 'HARVEST_MISS: no UIA root for the main window' }
    $byId = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::AutomationIdProperty, 'NavView')
    $strip = $root.FindFirst($TREE, $byId)
    if ($null -eq $strip) { throw 'HARVEST_MISS: no strip with AutomationId NavView' }
    $byType = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::ControlTypeProperty, $CTRL::ListItem)
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($el in $strip.FindAll($TREE, $byType)) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $names.Add($el.Current.Name)
    }
    return @($names)
}

# The horizontal strip's tabs by name, off the automation tree: TabView
# publishes each TabViewItem as a TabItem, named from the same accessible
# text the vertical rows carry.
function Get-UiaTabNames {
    $root = $UIA::FromHandle([SeamWin]::P($script:MainHwnd64))
    if ($null -eq $root) { throw 'HARVEST_MISS: no UIA root for the main window' }
    $byType = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::ControlTypeProperty, $CTRL::TabItem)
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($el in $root.FindAll($TREE, $byType)) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $names.Add($el.Current.Name)
    }
    return @($names)
}

# The seam's per-tab label readout, polled until $Until says the app has
# settled. Shell startup is not instant and neither is a cd: a deadline is
# the honest way to wait for a shell, and the failure carries the last
# thing seen so a miss reads as a finding rather than a timeout.
function Wait-Label($s, [scriptblock]$Until, [string]$What, [int]$Seconds = 45) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $resp = Invoke-SeamCommandQuiet $s @{ op = 'tab-labels' }
        $last = $resp.labels[0]
        if (& $Until $last) { return $last }
        Start-Sleep -Milliseconds 400
    }
    throw ("PRODUCT_FAIL: {0}; last saw cwd='{1}' rendered='{2}' title='{3}' shellTitle='{4}'" -f
        $What, $last.cwd, $last.rendered, $last.title, $last.shellTitle)
}

# Invoke-SeamCommand prints a line per call; a poll would drown the log.
function Invoke-SeamCommandQuiet($s, [hashtable]$Command) {
    Send-SeamCommand $s $Command
    return Receive-SeamResponse $s $Command['op']
}

# A shell cannot report its directory without the integration scripts, and
# the Debug layout ships no share/ghostty tree for resourcesDir() to find
# (`zig build -Dapp-runtime=none` installs the dll, not the resources). In a
# Debug build resourcesDir() falls back to GHOSTTY_RESOURCES_DIR, so stage
# the repo's own src/shell-integration under one and point at it. This is a
# harness compensation for the build layout, NOT part of what is under test:
# the scripts staged here are the ones an installed build ships.
function Enter-StagedResources {
    $repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    $src = Join-Path $repo 'src\shell-integration'
    if (-not (Test-Path $src)) { throw "HARNESS: no shell-integration tree at $src" }
    $stage = Join-Path $env:TEMP "wintty-cwd-res-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item $src -Destination (Join-Path $stage 'shell-integration') -Recurse -Force
    $prior = if (Test-Path Env:GHOSTTY_RESOURCES_DIR) { $env:GHOSTTY_RESOURCES_DIR } else { $null }
    $env:GHOSTTY_RESOURCES_DIR = $stage
    return @{ Stage = $stage; Prior = $prior }
}

function Exit-StagedResources($res) {
    if ($null -ne $res.Prior) { $env:GHOSTTY_RESOURCES_DIR = $res.Prior }
    else { Remove-Item Env:GHOSTTY_RESOURCES_DIR -ErrorAction SilentlyContinue }
    Remove-Item $res.Stage -Recurse -Force -ErrorAction SilentlyContinue
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$script:Scenarios = [System.Collections.Generic.List[object]]::new()


function New-ProbeDir([string]$Leaf, [string]$Root = $OutDir) {
    # A folder name nothing else in the product could produce, so a label that
    # reads it can only have come from the cd. Rooted at $OutDir rather than
    # $TEMP because $env:TEMP is handed to us in 8.3 form
    # (C:\Users\ALESSA~1\...) while a shell reports the long form, and the
    # assert compares the reported directory to this one literally. A scenario
    # that wants the ~ collapse roots its probe under the profile instead.
    $path = Join-Path $Root "wintty-cwd-probe-$Leaf"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

# The directory the product writes as ~. The known-folder API rather than
# $env:USERPROFILE, because that is what TabManager reads, and the two can
# disagree on a redirected profile.
$ProfileDir = [Environment]::GetFolderPath('UserProfile').TrimEnd('\')

function Invoke-Scenario(
    [string]$Name, [string]$Profile, [string]$WantIcon, [string]$WantIconTooltip, [scriptblock]$Body,
    [string]$ProbeRoot = $OutDir, [string]$ConfigExtra = '') {
    $probe = New-ProbeDir $Name $ProbeRoot
    $folder = Split-Path -Leaf $probe
    # The tooltip is the whole directory with the user's home written as ~,
    # so the expectation collapses the same prefix the product does: a probe
    # under the profile wants the ~ form, one outside it the path itself.
    $wantTip = if ($probe.StartsWith("$ProfileDir\", [StringComparison]::OrdinalIgnoreCase)) {
        '~' + $probe.Substring($ProfileDir.Length)
    } else { $probe }
    # default-profile is the only way to pin the first tab's shell: the
    # top-level `command` key loses to whatever profile discovery ranks first
    # (ordinal, so "cmd" wins). The shell scenarios name a DISCOVERED profile,
    # so the tab carries the probe's own interpreter icon -- the thing the
    # icon assert is about; a scenario may declare its own through $ConfigExtra.
    $config = @"
windows-single-instance = true
window-save-state = never
vertical-tabs = true
vertical-tabs-hover-expand = false
default-profile = $Profile
$ConfigExtra
"@
    $crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
    $entry = [ordered]@{ name = $Name; ok = $false; class = ''; error = ''; cwd = ''; rendered = ''; renderedH = ''; tooltip = ''; iconTooltip = ''; icon = ''; homeGlyph = $false; sawStarting = $false }
    $s = $null
    Write-Host "=== scenario $Name (profile $Profile) ==="
    try {
        Assert-NoWintty -Context "The cwd label scenario '$Name'"
        # -AllowInput because this harness types a `cd` into the shell and
        # reads the label that comes back; send-text is off without it. It is
        # the only harness in the suite that needs the shell to run anything.
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput
        $script:MainHwnd64 = $s.Hwnd64

        # Every field this scenario reads has to be one the seam still
        # writes: absent, a boolean reads as $false and every assertion
        # below it passes on silence.
        $probeLabel = (Invoke-SeamCommandQuiet $s @{ op = 'tab-labels' }).labels[0]
        foreach ($field in 'settling', 'home', 'hover', 'renderedHomeGlyph') {
            if ($null -eq $probeLabel.PSObject.Properties[$field]) {
                throw "HARNESS: the seam no longer reports '$field'; assertions on it would be dead"
            }
        }

        # A tab wears the app's own icon while it starts. Catching that state
        # is what makes the settled assertion below mean something: without
        # it, a build that never starts a tab passes every line that follows.
        # A machine fast enough to have finished already is a miss, not a
        # product finding -- said out loud rather than silently skipped.
        if ($probeLabel.settling) {
            if ($probeLabel.iconKey -ne 'bundled:default') {
                throw ("PRODUCT_FAIL: {0}: a starting tab wears '{1}', wanted the app's own icon" -f
                    $Name, $probeLabel.iconKey)
            }
            $entry.sawStarting = $true
        } else {
            Write-Host ("  note: {0} had already started when first polled; the starting state was not observed" -f $Name)
        }

        # The profile's icon is only the tab's answer once the start is over,
        # so waiting is what makes the next assert about the profile rather
        # than about a race -- and it is the check that the borrowed icon is
        # handed back.
        $opened = Wait-Label $s { param($l) -not $l.settling } `
            "$Name never finished starting (no first render and no report from its shell)"

        # The right shell has to be the one under test. The profile's icon
        # names the interpreter, so this doubles as the "did default-profile
        # take" check -- an unresolvable id silently falls through to the
        # first discovered profile, which would test cmd twice.
        if ($opened.iconKey -ne $WantIcon) {
            throw ("PRODUCT_FAIL: {0}: the tab opened on '{1}', wanted the '{2}' profile" -f
                $Name, $opened.iconKey, $WantIcon)
        }

        # The shell has to reach a prompt before it can report anything.
        $first = Wait-Label $s { param($l) $null -ne $l.cwd } `
            "$Name never reported a directory at its first prompt (the native OSC 7 / OSC 9;9 arms are dead)"

        # A shell that starts in the user's own directory is the one tab that
        # draws the home glyph instead of printing a name. Both branches
        # assert: the harness decides for itself whether the reported
        # directory IS the profile directory, requires the product to agree,
        # and then requires the drawing to match. A skip would otherwise read
        # as a pass on a machine whose shells start somewhere else.
        $atHome = $first.cwd.TrimEnd('\', '/') -eq $ProfileDir
        if ($first.home -ne $atHome) {
            throw ("PRODUCT_FAIL: {0}: the tab says home={1} for '{2}' while the profile directory is '{3}'" -f
                $Name, $first.home, $first.cwd, $ProfileDir)
        }
        if ($atHome) {
            if (-not $first.renderedHomeGlyph -or $first.rendered -ne '') {
                throw ("PRODUCT_FAIL: {0}: at home the row should draw the glyph and print nothing; glyph={1} rendered='{2}'" -f
                    $Name, $first.renderedHomeGlyph, $first.rendered)
            }
            $entry.homeGlyph = $true
        } else {
            if ($first.renderedHomeGlyph -or $first.rendered -eq '') {
                throw ("PRODUCT_FAIL: {0}: away from home the row should print its name; glyph={1} rendered='{2}' cwd='{3}'" -f
                    $Name, $first.renderedHomeGlyph, $first.rendered, $first.cwd)
            }
            $entry.homeGlyph = 'not exercised: the shell did not start at the profile directory'
        }

        # The scenario's own way of putting the shell in $probe.
        & $Body $s $probe

        $label = Wait-Label $s { param($l) $l.cwd -eq $probe } `
            "$Name did not report '$probe'"
        $entry.cwd = $label.cwd

        # (ii) the strip's own text, from the row's TextBlock. Away from home
        # the glyph is down and the name is printed again.
        if ($label.rendered -ne $folder -or $label.renderedHomeGlyph) {
            throw ("PRODUCT_FAIL: {0}: the strip renders '{1}' (glyph={4}), the folder is '{2}' (cwd='{3}')" -f
                $Name, $label.rendered, $folder, $label.cwd, $label.renderedHomeGlyph)
        }
        $entry.rendered = $label.rendered

        # (iii) the tooltip the row's TextBlock is carrying: the whole
        # directory, where the label shows only its leaf.
        if ($label.renderedTooltip -ne $wantTip) {
            throw ("PRODUCT_FAIL: {0}: the row's tooltip is '{1}', wanted '{2}'" -f
                $Name, $label.renderedTooltip, $wantTip)
        }
        $entry.tooltip = $label.renderedTooltip

        # A tab with a directory is owed a hover, and it is the same text:
        # the null case (a hover that would only repeat the label) is what
        # the model tests carry, since a shell that reports cannot produce it.
        if ($label.hover -ne $label.tooltip) {
            throw ("PRODUCT_FAIL: {0}: the hover is '{1}' while the tooltip is '{2}'" -f
                $Name, $label.hover, $label.tooltip)
        }

        # (iv) the icon's tooltip, exactly. A discovered profile is named after
        # its shell, so there the tooltip says the shell once and this cannot
        # tell shell from profile; the declared-profile scenario is the one
        # that can, and it wants the shell on its own line above the name.
        if ("$($label.iconTooltip)" -notlike $WantIconTooltip) {
            throw ("PRODUCT_FAIL: {0}: the icon's tooltip is '{1}', wanted '{2}'" -f
                $Name, ($label.iconTooltip -replace "`n", '\n'), ($WantIconTooltip -replace "`n", '\n'))
        }
        $entry.iconTooltip = $label.iconTooltip

        # ... and again through UIA, which shares no code with the readout
        # above.
        $names = Get-UiaRowNames
        if (@($names | Where-Object { $_ -like "*$folder*" }).Count -eq 0) {
            throw ("PRODUCT_FAIL: {0}: no rendered row names the folder '{1}'; rows are [{2}]" -f
                $Name, $folder, ($names -join ', '))
        }

        # (v) the same label and tooltip on the HORIZONTAL strip. The layout
        # flips the way the chord flips it, and the readout is the
        # TabViewItem's header TextBlock and its own tooltip -- a surface
        # that shares no code with the vertical row.
        [void](Invoke-SeamCommandQuiet $s @{ op = 'toggle-layout' })
        $flat = Wait-Label $s { param($l) $l.renderedH -eq $folder } `
            "${Name}: after the layout switch the horizontal strip never rendered '$folder'"
        if ($flat.renderedTooltipH -ne $wantTip) {
            throw ("PRODUCT_FAIL: {0}: the horizontal item's tooltip is '{1}', wanted '{2}'" -f
                $Name, $flat.renderedTooltipH, $wantTip)
        }
        if ($flat.renderedHomeGlyphH) {
            throw ("PRODUCT_FAIL: {0}: the horizontal tab draws the home glyph while sitting in '{1}'" -f
                $Name, $flat.cwd)
        }
        $entry.renderedH = $flat.renderedH
        $tabNames = Get-UiaTabNames
        if (@($tabNames | Where-Object { $_ -like "*$folder*" }).Count -eq 0) {
            throw ("PRODUCT_FAIL: {0}: no horizontal tab names the folder '{1}'; tabs are [{2}]" -f
                $Name, $folder, ($tabNames -join ', '))
        }

        # The interpreter icon: a decoded image on the row, and a key that
        # tells this shell apart from the other one.
        if ($label.renderedIcon -ne 'image') {
            throw ("PRODUCT_FAIL: {0}: the row's icon is '{1}', wanted a decoded image" -f
                $Name, $label.renderedIcon)
        }
        $entry.icon = $label.iconKey

        if ($s.Proc.HasExited) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $Name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host ("PASS {0}: cwd='{1}' label='{2}' (horizontal '{6}') tooltip='{3}' icon='{4}' ({5}) homeGlyph={7} sawStarting={8}" -f
            $Name, $entry.cwd, $entry.rendered, $entry.tooltip, $entry.icon,
            ($entry.iconTooltip -replace "`n", ' / '), $entry.renderedH, $entry.homeGlyph,
            $entry.sawStarting) -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $Name [$($entry.class)]: $msg" -ForegroundColor Red
        if ($null -ne $s -and -not $s.Proc.HasExited) {
            try {
                $rc = [SeamWin]::RectOf($script:MainHwnd64)
                if ($null -ne $rc) {
                    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
                    $bmp.Save((Join-Path $OutDir "fail-$Name.png"))
                    $g.Dispose(); $bmp.Dispose()
                }
            } catch { }
        }
    } finally {
        if ($null -ne $s) { Stop-SeamSession $s }
        Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $crashPath) {
        if ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp) {
            $entry.ok = $false
            $entry.class = 'product'
            $entry.error += ' | crash.log grew during the scenario'
            Write-Host "FAIL $Name [product]: crash.log grew" -ForegroundColor Red
        }
    }
    $script:Scenarios.Add([pscustomobject]$entry)
}

# A reported cwd is a SPAWN directory: Duplicate Tab, Reopen Closed Tab and
# session restore all hand it to CreateProcess. A UNC directory therefore
# makes Windows open an SMB connection to the server the path names, and
# authenticate to it -- so adopting a UNC cwd on the strength of bytes alone
# would let anything that can write to the pty pick who receives the user's
# credentials. `cat` of a hostile file is enough; so is any remote session.
#
# The injected OSC 9;9 is byte-for-byte what the PowerShell integration emits
# at line 76 of powershell/ghostty-integration.ps1, so this drives the real
# arm and not a lookalike.
#
# Two reports go out in ONE command, a local path then the remote one, and
# the assert is on the SETTLED value. That is what makes the scenario
# non-vacuous: if the check were deleted, the cwd ends on the UNC path; if
# the whole raw arm were dead, it never reaches the local probe at all and
# the scenario fails as a miss rather than passing on silence.
#
# The host is `.invalid` (RFC 2606), which never resolves -- if this harness
# ever does provoke a lookup, it provokes it against nothing.
$UncHostile = '\\wintty-unc-refused.invalid\share'

function Invoke-UncRefusedScenario {
    $name = 'unc-refused'
    $local = New-ProbeDir 'unc-local'
    $config = @"
windows-single-instance = true
window-save-state = never
vertical-tabs = true
vertical-tabs-hover-expand = false
default-profile = pwsh-7
"@
    $crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
    $entry = [ordered]@{ name = $name; ok = $false; class = ''; error = ''; cwd = ''; rendered = ''; tooltip = ''; iconTooltip = ''; icon = '' }
    $s = $null
    Write-Host "=== scenario $name ==="
    try {
        Assert-NoWintty -Context "The cwd label scenario '$name'"
        # -AllowInput for the same reason as the label scenarios: this one
        # types the prompt replacement and the injected reports into the shell.
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput
        $script:MainHwnd64 = $s.Hwnd64

        $first = Wait-Label $s { param($l) $null -ne $l.cwd } `
            "$name never reported a directory at its first prompt"

        # The integration reports from its own `global:prompt`, which fires
        # after every command and would overwrite an injected value before it
        # could be read. Replacing the function stops the reports, so what the
        # injection sets is what stays set.
        [void](Invoke-SeamCommand $s @{ op = 'send-text'; text = "function global:prompt { 'X> ' }`r" })
        Start-Sleep -Milliseconds 800

        $osc = "[Console]::Write(`"``e]9;9;$local``a`"); [Console]::Write(`"``e]9;9;$UncHostile``a`")"
        [void](Invoke-SeamCommand $s @{ op = 'send-text'; text = "$osc`r" })

        # Wait for the pair to land, then let the app settle before reading:
        # polling for the local probe alone would sample between the two
        # writes and call an adopted UNC path a pass.
        [void](Wait-Label $s { param($l) $l.cwd -ne $first.cwd } `
            "${name}: neither injected report reached the app (the raw OSC 9;9 arm is dead)")
        Start-Sleep -Seconds 2
        $settled = (Invoke-SeamCommandQuiet $s @{ op = 'tab-labels' }).labels[0]
        $entry.cwd = $settled.cwd

        if ($settled.cwd -eq $UncHostile) {
            throw ("PRODUCT_FAIL: {0}: the pane adopted '{1}' as its cwd; a spawn there authenticates to a host the pty named" -f
                $name, $settled.cwd)
        }
        if ($settled.cwd -ne $local) {
            throw ("PRODUCT_FAIL: {0}: expected the local report '{1}' to stand, saw '{2}'" -f
                $name, $local, $settled.cwd)
        }
        $entry.rendered = $settled.rendered

        if ($s.Proc.HasExited) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host ("PASS {0}: refused '{1}', kept '{2}'" -f $name, $UncHostile, $entry.cwd) -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $name [$($entry.class)]: $msg" -ForegroundColor Red
    } finally {
        if ($null -ne $s) { Stop-SeamSession $s }
        Remove-Item $local -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $crashPath) {
        if ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp) {
            $entry.ok = $false
            $entry.class = 'product'
            $entry.error += ' | crash.log grew during the scenario'
            Write-Host "FAIL $name [product]: crash.log grew" -ForegroundColor Red
        }
    }
    $script:Scenarios.Add([pscustomobject]$entry)
}

# cmd drives the OSC 9;9 arm -- `PROMPT $p` is the only cwd report it has,
# and it is a raw Windows path. `cd /d` because TEMP may be on another drive,
# quoted because it may hold a space.
#
# pwsh drives the OSC 7 arm: its integration sends a `file://HOST/c:/dir` URL
# at every prompt, so a plain `cd` covers a shape cmd can never produce. Its
# probe sits under the profile, so this is also the leg that sees the ~ form
# in the rendered tooltip while the reported directory stays absolute.
#
# The third shell scenario declares a profile whose name says nothing about
# its shell, which is the only way to watch the icon's tooltip put the shell
# above the profile name rather than repeat the profile.
$staged = Enter-StagedResources
try {
    Invoke-Scenario 'cmd' 'cmd' 'bundled:cmd' 'Command Prompt' {
        param($s, $probe)
        [void](Invoke-SeamCommand $s @{ op = 'send-text'; text = "cd /d `"$probe`"`r" })
    }
    Invoke-Scenario 'pwsh' 'pwsh-7' 'bundled:pwsh' 'PowerShell*' {
        param($s, $probe)
        [void](Invoke-SeamCommand $s @{ op = 'send-text'; text = "cd `"$probe`"`r" })
    } -ProbeRoot $ProfileDir
    Invoke-Scenario 'declared' 'probe' 'brand:pwsh' "PowerShell`nProbe" {
        param($s, $probe)
        [void](Invoke-SeamCommand $s @{ op = 'send-text'; text = "cd `"$probe`"`r" })
    } -ConfigExtra "profile.probe.name = Probe`nprofile.probe.command = pwsh.exe"
    Invoke-UncRefusedScenario
} finally { Exit-StagedResources $staged }

# The icon has to name the interpreter, which means two shells must not wear
# the same one.
$cmdIcon = ($script:Scenarios | Where-Object { $_.name -eq 'cmd' } | Select-Object -First 1).icon
$pwshIcon = ($script:Scenarios | Where-Object { $_.name -eq 'pwsh' } | Select-Object -First 1).icon
if ($cmdIcon -and $pwshIcon) {
    $same = $cmdIcon -eq $pwshIcon
    $script:Scenarios.Add([pscustomobject]@{
        name = 'icon-differs'; ok = (-not $same)
        class = $(if ($same) { 'product' } else { '' })
        error = $(if ($same) { "both shells wear '$cmdIcon'" } else { '' })
        cwd = ''; rendered = ''; icon = "$cmdIcon vs $pwshIcon"
    })
    if ($same) { Write-Host "FAIL icon-differs [product]: both wear '$cmdIcon'" -ForegroundColor Red }
    else { Write-Host "PASS icon-differs: $cmdIcon vs $pwshIcon" -ForegroundColor Green }
}

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
    scenarios = $script:Scenarios
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'scenario       verdict'
Write-Host '-------------  -------'
foreach ($sc in $script:Scenarios) {
    Write-Host ("{0,-14} {1}" -f $sc.name, $(if ($sc.ok) { 'PASS' } else { "FAIL ($($sc.class))" }))
}

if (@($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if (@($script:Scenarios | Where-Object { -not $_.ok }).Count -gt 0) { exit 1 }
exit 0
