#requires -Version 7
# The custom-shader banner, driven from both sides.
#
# "Custom shader not applied" is a warning about something the user asked for
# and did not get. It is worth exactly as much as its two failure modes are
# rare, and both of them are reachable:
#
#   1. It fires with no custom-shader configured at all. The C# action-tag
#      enum had drifted one place from include/ghostty.h after an upstream
#      sync, so GHOSTTY_ACTION_FIRST_RENDER - raised once per surface on first
#      paint - arrived at the CustomShaderFailed handler, and its absent
#      payload read back as LoadFailed. Every new tab produced the banner.
#
#   2. It goes silent. The same drift in the other direction, or a lost
#      mailbox push, leaves a configured shader doing nothing with no trace
#      but a log.warn nobody opens, which is the whole reason the banner
#      exists.
#
# So this checks the banner in both directions against a config it stages
# itself, rather than only asserting the quiet case:
#
#   absent / empty custom-shader  ->  no notice of any kind, ever
#   unreadable or untranslatable  ->  the custom-shader notice, reason "load"
#   a shader that does translate  ->  no "load" notice
#
# The last one is deliberately the weakest. A missing compiler or a pipeline
# the GPU refuses are properties of the machine as much as of the build, so
# they are recorded and printed but are not findings here; only the
# load/translate step, which is pure CPU with no driver in it, is asserted.
#
# Anti-vacuity: a run whose detector is broken would pass every negative case
# by finding nothing, which is the failure this suite exists not to have. So a
# positive case always runs FIRST - if the detector cannot see a banner that
# should be there, the run says so instead of collecting four green quiet
# cases. UIA reachability is proven separately against a chrome element, so a
# dead UIA connection leaves with 1 (nothing known) rather than 2 (a finding).
#
# Seeded: -Seed replays the case order, the tab counts and the path spelling.
#
# Exit codes: 0 clean, 2 findings in the build under test, 1 could not run.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 0
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

# Same convention as the other harnesses here: a PRODUCT_FAIL throw is a
# defect in the build and has to leave with 2, and anything else is a run that
# judged nothing and leaves with 1 for the runner to retry. `break` rethrows
# the rest so a genuine harness failure keeps its 1.
trap {
    if ("$_" -like 'PRODUCT_FAIL*') {
        Write-Host "$_" -ForegroundColor Red
        exit 2
    }
    break
}

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

if ($Seed -eq 0) { $Seed = Get-Random -Minimum 1 -Maximum 999999 }
$rng = [System.Random]::new($Seed)
Write-Host "seed=$Seed"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SNz {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }
    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }
    public static string ClassOf(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }
    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString();
    }
}
'@

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [SNz+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][SNz]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [SNz]::IsWindowVisible($h)) { return $true }
        if ([SNz]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [SNz]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[SNz]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][SNz]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Test-SplashVisible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [SNz+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][SNz]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([SNz]::ClassOf($hwnd) -eq 'WinttySplash' -and [SNz]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][SNz]::EnumWindows($cb,[IntPtr]::Zero)
    return $script:splashSeen
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(40)
    $got = $null
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $got = @(Get-WinUiWindows ([uint32]$proc.Id)) | Select-Object -First 1
        if ($got) { break }
    }
    if (-not $got) { throw "HARVEST_MISS: no WinUI hwnd" }
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL during splash" }
        if (Test-SplashVisible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Test-SplashVisible $proc.Id)) { return $got }
    }
    throw "HARVEST_MISS: splash never dropped"
}

function Save-Shot([int64]$Hwnd64, [string]$Name) {
    $rc = [SNz]::RectOf($Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

function Get-UiaRoot([int64]$Hwnd64) {
    try { return [System.Windows.Automation.AutomationElement]::FromHandle([SNz]::P($Hwnd64)) }
    catch { return $null }
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# Every named element under the window, walked exactly the way Get-Notices
# walks it. The reachability probe uses this so it exercises the oracle's own
# call path rather than a cheaper one that could succeed where that fails.
function Get-DescendantNames([int64]$Hwnd64) {
    $root = Get-UiaRoot $Hwnd64
    if ($null -eq $root) { throw "HARVEST_MISS: no UIA root for hwnd $Hwnd64" }
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                                  [System.Windows.Automation.Condition]::TrueCondition)) {
        $n = try { [string]$el.Current.Name } catch { '' }
        if ($n) { $out.Add($n) }
    }
    return $out.ToArray()
}

function Measure-TabItems([int64]$Hwnd64) {
    $root = Get-UiaRoot $Hwnd64
    if ($null -eq $root) { return 0 }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)).Count
}

# Every notice banner currently on the window, whatever raised it.
#
# Matched on AutomationId rather than on the banner's visible title:
# NotificationHost stamps each InfoBar "Notice_<dedup key>", and the title is
# user-facing copy that a wording change would move out from under us. Reading
# every notice rather than only the custom-shader one is what lets the quiet
# cases assert "no banner at all" - a misrouted action tag can land on any
# handler, not only this one.
function Get-Notices([int64]$Hwnd64) {
    $root = Get-UiaRoot $Hwnd64
    if ($null -eq $root) { throw "HARVEST_MISS: no UIA root for hwnd $Hwnd64" }
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                         [System.Windows.Automation.Condition]::TrueCondition)
    $out = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $all) {
        $id = try { [string]$el.Current.AutomationId } catch { '' }
        if ($id -ne 'Notice' -and -not $id.StartsWith('Notice_')) { continue }
        # The InfoBar's own UIA Name is the title; the body is a child
        # TextBlock, so the reason only shows up by walking underneath it.
        $parts = [System.Collections.Generic.List[string]]::new()
        $name = try { [string]$el.Current.Name } catch { '' }
        if ($name) { $parts.Add($name) }
        foreach ($kid in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                                     [System.Windows.Automation.Condition]::TrueCondition)) {
            $kn = try { [string]$kid.Current.Name } catch { '' }
            if ($kn) { $parts.Add($kn) }
        }
        $out.Add([pscustomobject]@{ Id = $id; Text = ($parts -join ' | ') })
    }
    # Returned unrolled, so the caller's @() rebuilds it at any length. Wrapping
    # it with a unary comma instead hands the caller ONE object that happens to
    # be an array: `.Id` still answers through member enumeration, so the ids
    # printed fine while every count read 1.
    return $out.ToArray()
}

# Which CustomShaderFailure a banner is reporting, read from its copy. Keyed
# on a distinctive fragment of each message rather than the whole string, so a
# copy edit degrades to "unknown" rather than to "load".
function Get-NoticeReason([string]$Text) {
    if ($Text -match 'could not read or translate') { return 'load' }
    if ($Text -match 'dxcompiler\.dll') { return 'compiler-unavailable' }
    if ($Text -match 'did not compile') { return 'compile' }
    if ($Text -match 'graphics pipeline') { return 'pipeline' }
    return 'unknown'
}

$shaderNoticeId = 'Notice_custom-shader'

# ---- staging --------------------------------------------------------------
# The gate goes above the staging, not below it. Refusing over an open Wintty
# is the most common way this run ends, and everything under it writes to disk.
Assert-NoWintty

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$validSrc = Join-Path $repoRoot 'src\renderer\shaders\test_passthrough.glsl'
$invalidSrc = Join-Path $repoRoot 'src\renderer\shaders\test_shadertoy_invalid.glsl'
foreach ($f in @($validSrc, $invalidSrc)) {
    if (-not (Test-Path -LiteralPath $f)) { throw "HARVEST_MISS: fixture shader missing: $f" }
}

$stage = Join-Path $env:TEMP ("wintty-shader-fuzz-{0:HHmmss}" -f (Get-Date))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $validSrc -Destination (Join-Path $stage 'valid.glsl')
Copy-Item -LiteralPath $invalidSrc -Destination (Join-Path $stage 'invalid.glsl')
$goneShader = Join-Path $stage 'not-here.glsl'

# The path spelling is fuzzed for one specific confusion, not for path handling
# in general. All three name a file that is not there, so what separates them is
# whether the config parser produced a path at all: a spelling it fails to parse
# leaves custom-shader empty, `configured` is false in the renderer, and NO
# banner is raised - which this case reads as the banner having gone silent. The
# quoted-with-a-space spelling is the one that could plausibly do that.
function Get-MissingPathSpelling([int]$Pick) {
    switch ($Pick) {
        0 { return $goneShader }
        1 { return ($goneShader -replace '\\', '/') }
        default { return '"' + (Join-Path $stage 'not here.glsl') + '"' }
    }
}

# expect: 'none'   no notice of any kind may appear
#         'load'   the custom-shader notice must appear, reporting a load failure
#         'noload' anything except a load failure is acceptable (see the header)
$cases = @(
    [ordered]@{ id = 'absent'; expect = 'none'; line = $null }
    [ordered]@{ id = 'empty'; expect = 'none'; line = 'custom-shader = ' }
    [ordered]@{ id = 'missing'; expect = 'load'; line = $null }   # filled in below
    [ordered]@{ id = 'invalid'; expect = 'load'; line = ('custom-shader = ' + (Join-Path $stage 'invalid.glsl')) }
    [ordered]@{ id = 'valid'; expect = 'noload'; line = ('custom-shader = ' + (Join-Path $stage 'valid.glsl')) }
)
$spelling = $rng.Next(0, 3)
($cases | Where-Object { $_.id -eq 'missing' }).line = 'custom-shader = ' + (Get-MissingPathSpelling $spelling)
Write-Host "missing-path spelling=$spelling"

# A positive case first, always. A detector that cannot see a banner would
# otherwise report every quiet case as a pass, and the run would be green
# precisely when it is worthless.
$positives = @($cases | Where-Object { $_.expect -eq 'load' })
$lead = $positives[$rng.Next(0, $positives.Count)]
$rest = @($cases | Where-Object { $_.id -ne $lead.id } | Sort-Object { $rng.Next() })
$order = @($lead) + $rest
Write-Host ("order=" + (($order | ForEach-Object { $_.id }) -join ','))

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }

# The quiet cases assert that NO banner appears, so every other thing that can
# legitimately raise one has to be staged too, not just the config. NO_COLOR is
# the one that bites: set it in the shell you run this from and Wintty shows its
# NO_COLOR banner, correctly, and the run reports two findings that are nothing
# to do with shaders. Cleared for the children and restored below, same as XDG.
#
# Keeping the oracle at "no banner at all" rather than "no shader banner" is
# deliberate. A misrouted action tag lands wherever the ordinals put it, which
# is not necessarily the handler you were watching.
$originalNoColorSet = Test-Path Env:NO_COLOR
$originalNoColor = if ($originalNoColorSet) { $env:NO_COLOR } else { $null }

$tempXdg = Join-Path $stage 'xdg'
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
$configPath = Join-Path $tempXdg 'wintty\config.wintty'

# One launch, one case. Returns every notice the window showed.
function Invoke-Case($Case, [int]$ExtraTabs, [string]$Exe) {
    $body = @(
        '# staged by shader-notice-fuzz.ps1'
        # Not single-instance: this harness launches five times in a row, and a
        # survivor from the previous case would otherwise adopt the next
        # launch, which would then be judged against the wrong config.
        'windows-single-instance = false'
        'window-save-state = never'
    )
    if ($Case.line) { $body += $Case.line }
    [IO.File]::WriteAllText($configPath, ($body -join "`r`n") + "`r`n")

    $proc = $null
    $stamp = Get-WinttyLaunchStamp
    try {
        $env:XDG_CONFIG_HOME = $tempXdg
        Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
        $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
        $pid32 = [uint32]$proc.Id
        [void](Wait-Ready $proc)
        Start-Sleep -Milliseconds 800
        $main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
        if (-not $main) { throw "HARVEST_MISS: window vanished after ready" }
        $hwnd64 = [int64]$main.Hwnd64

        # Reachability, proven against chrome rather than against the thing
        # under test. Without this a UIA connection that returns nothing looks
        # exactly like a build that raises no banner.
        #
        # Probed through the SAME full-descendant FindAll the oracle uses, not
        # through a targeted FindFirst: those are different UIA paths, and a
        # client whose FindAll truncates would pass a FindFirst probe and then
        # report "no banner" for every quiet case.
        $names = Get-DescendantNames $hwnd64
        if ($names -notcontains 'New tab') {
            throw ("HARVEST_MISS: a full-descendant walk of hwnd $hwnd64 returned " +
                   "$($names.Count) named elements and no 'New tab'")
        }

        # Extra surfaces, because the action this banner rides in on is raised
        # per surface. The regression that prompted this harness produced one
        # banner per new tab, so a single-surface run understates it.
        #
        # Counted, not assumed. A silent break here would leave the row saying
        # extraTabs=2 for a run that opened none, which is the per-surface
        # dimension quietly not being tested.
        $tabsBefore = Measure-TabItems $hwnd64
        for ($i = 0; $i -lt $ExtraTabs; $i++) {
            $btn = Find-Name (Get-UiaRoot $hwnd64) 'New tab'
            if ($null -eq $btn) { break }
            try {
                $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            } catch { break }
            Start-Sleep -Milliseconds 700
        }
        $tabsOpened = (Measure-TabItems $hwnd64) - $tabsBefore
        if ($tabsOpened -lt $ExtraTabs) {
            throw ("HARVEST_MISS: asked for $ExtraTabs extra tabs, opened $tabsOpened; " +
                   "the per-surface dimension was not exercised")
        }

        # A positive case polls until the banner turns up; a quiet case has to
        # wait out the whole window before it may conclude anything, because
        # "not yet" and "never" look identical early.
        $seen = @()
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline) {
            $proc.Refresh()
            if ($proc.HasExited) { throw "PRODUCT_FAIL exited during case $($Case.id): exit=$($proc.ExitCode)" }
            foreach ($n in @(Get-Notices $hwnd64)) {
                if (-not @($seen | Where-Object { $_.Id -eq $n.Id -and $_.Text -eq $n.Text })) { $seen += $n }
            }
            if ($Case.expect -eq 'load' -and @($seen | Where-Object { $_.Id -eq $shaderNoticeId })) { break }
            Start-Sleep -Milliseconds 600
        }
        Save-Shot $hwnd64 ("case-" + $Case.id)
        # Wrapped in an object rather than returned as a bare array: a function
        # that returns an array is at the mercy of anything else that leaks into
        # the output stream, and this one runs a lot of code.
        return [pscustomobject]@{ Notices = @($seen) }
    }
    finally {
        if ($null -ne $proc) {
            $proc.Refresh()
            if (-not $proc.HasExited) { try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { } }
        }
        Stop-WinttyStartedAfter -Since $stamp -ExePath $Exe
        Start-Sleep -Milliseconds 600
    }
}

$findings = [System.Collections.Generic.List[string]]::new()
$caseErrors = [System.Collections.Generic.List[string]]::new()
$rows = [System.Collections.Generic.List[object]]::new()
$detectorProven = $false

try {
    foreach ($case in $order) {
        $extraTabs = $rng.Next(0, 3)
        Write-Host "--- case=$($case.id) expect=$($case.expect) extraTabs=$extraTabs"

        # Per case, so one case that cannot run does not throw away what the
        # cases before it already found. Letting it escape lost every finding
        # collected so far AND the whole report, and the run left with 1 -
        # "nothing is known about the product" - having in fact observed a
        # defect. Findings outrank a case that could not run, which is the
        # same order fuzz-suite.ps1 puts them in.
        try {
            $seen = @((Invoke-Case $case $extraTabs $ExePath).Notices)
        }
        catch {
            $note = "case '$($case.id)': $_"
            if ("$_" -like 'PRODUCT_FAIL*') { $findings.Add($note) } else { $caseErrors.Add($note) }
            Write-Host "    $note" -ForegroundColor Yellow
            $rows.Add([ordered]@{ case = $case.id; expect = $case.expect; extraTabs = $extraTabs; error = "$_" })
            continue
        }

        $shader = @($seen | Where-Object { $_.Id -eq $shaderNoticeId })
        $reasons = @($shader | ForEach-Object { Get-NoticeReason $_.Text })
        $ids = @($seen | ForEach-Object { $_.Id })
        Write-Host ("    notices=" + $seen.Count + " ids=" + ($ids -join ',') + " reasons=" + ($reasons -join ','))
        foreach ($n in $seen) { Write-Host ("    [" + $n.Id + "] " + $n.Text) }

        switch ($case.expect) {
            'none' {
                if ($seen.Count -gt 0) {
                    $findings.Add("case '$($case.id)' configures no shader yet raised " + ($ids -join ',') +
                                  " (reasons: " + ($reasons -join ',') + ")")
                }
            }
            'load' {
                if ($reasons -contains 'load') {
                    $detectorProven = $true
                } elseif ($shader.Count -gt 0) {
                    $findings.Add("case '$($case.id)' configures an unreadable shader and the banner appeared, but " +
                                  "with an unrecognised reason (" + ($reasons -join ',') + "); either the copy was " +
                                  "reworded or the failure is not the load one: " + ($shader[0].Text))
                } else {
                    $findings.Add("case '$($case.id)' configures an unreadable shader and no banner appeared " +
                                  "(saw: " + ($ids -join ',') + ")")
                }
            }
            'noload' {
                if ($reasons -contains 'load') {
                    $findings.Add("case '$($case.id)' configures a shader that translates, but the banner reports a load failure")
                }
            }
            default {
                # No silent fall-through. An expectation nobody classified means
                # the case ran and was judged against nothing, and without this
                # the run would report a green pass for it.
                throw "HARVEST_MISS: case '$($case.id)' has an unclassified expect '$($case.expect)'"
            }
        }

        $rows.Add([ordered]@{
            case = $case.id
            expect = $case.expect
            extraTabs = $extraTabs
            noticeIds = $ids
            reasons = $reasons
        })
    }

    # The verdict is only worth anything if a banner was seen at least once.
    # Everything else here is an absence, and an absence proves nothing about a
    # detector that never demonstrated it can see a presence. This is asserted
    # rather than left to the ordering above, because the ordering is a property
    # of the case table and the case table is editable.
    if (-not $detectorProven -and $caseErrors.Count -eq 0) {
        $findings.Add('no case ever produced the custom-shader banner, so nothing here shows the ' +
                      'detector works; every quiet result above is unverified')
    }
}
finally {
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($originalNoColorSet) { $env:NO_COLOR = $originalNoColor }
    else { Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force -LiteralPath $stage -ErrorAction SilentlyContinue

    # Written from the finally, so the report survives a throw from outside the
    # per-case catch above. The exit code is still decided below, on the paths
    # where there is one to decide.
    $crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
    if ($crashGrew) { $findings.Add('crash.log grew during the run') }

    [ordered]@{
        seed = $Seed
        spelling = $spelling
        detectorProven = $detectorProven
        crashGrew = $crashGrew
        cases = $rows
        findings = $findings
        caseErrors = $caseErrors
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json')
    Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
}

if ($findings.Count -gt 0) {
    Write-Host 'PRODUCT_FAIL:' -ForegroundColor Red
    $findings | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    if ($caseErrors.Count -gt 0) {
        Write-Host 'also, cases that could not run:' -ForegroundColor Yellow
        $caseErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
    Write-Host "replay with -Seed $Seed" -ForegroundColor Red
    exit 2
}
if ($caseErrors.Count -gt 0) {
    Write-Host 'cases that could not run, so their area is untested:' -ForegroundColor Yellow
    $caseErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 1
}
Write-Host "clean (seed $Seed)" -ForegroundColor Green
exit 0
