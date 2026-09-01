#requires -Version 7
<#
The first-layout-switch renderer crash, driven on purpose.

A crash was sighted twice in six filmstrip sessions, always at the first
layout toggle, and filed nowhere because nobody had reproduced it. The
sentry envelopes under %LOCALAPPDATA%\Wintty\crash all carry one stack:

    renderer/Thread.zig drawFrame
      renderer/generic.zig:1898 drawFrame
        renderer/directx12/buffer.zig:107 syncFromArrayLists  (grow path)
          renderer/directx12/buffer.zig:201 release           (ID3D12Resource::Release)
            D3D12SDKLayers.dll ...
              KERNELBASE!RaiseException          <- the process dies here

D3D12SDKLayers is the D3D12 debug layer, which device.zig enables on
Debug builds only. So the question this harness answers is not "does the
app crash" but "how often, and is the TOGGLE what does it" -- because a
first toggle is also the first surface resize, the first realization of
both tab strips, and the first time anything makes the per-frame cell
buffer outgrow itself and take the release path above.

Four arms, identical except for what happens after the tabs are seeded:

  toggle  seed the strip, fire ONE toggle-layout, dwell
  idle    seed the strip, fire nothing, dwell the same wall time
  bare    seed nothing, dwell the same wall time
  resize  seed the strip, shrink the window and then GROW it, dwell --
          no toggle at all, so this arm asks the follow-up question: is
          it the layout switch, or is it any growth of the terminal
          surface? MoveWindow only; no focus theft, no synthesized input.

Run the arms at the same -Attempts and compare. A hit rate that is
non-zero in `toggle` and zero in the others is the claim that the switch
is implicated; equal rates across arms mean the crash belongs to cold
startup and the toggle was a coincidence of when people were looking.

Measured 2026-09-01, Debug x64, this repo's C# at f2d88607e8 over the
ABI-identical libghostty from b786e0d253, AMD Radeon:

    toggle   3 of 36     all three exit 2173 (0x87D)
    idle     0 of 8
    bare     0 of 8
    resize   0 of 20     growth verified: 900x620 -> 1700x1050, and the
                         launch geometry was 1280x820, so the grow really
                         did exceed the size the buffers were sized for

Every crash landed in the toggle arm and none in 36 control attempts,
but read that honestly: 3-of-36 against 0-of-36 is p ~ 0.24 by Fisher's
exact test, so the arms alone do not settle it. The reason to believe the
toggle anyway is mechanical rather than statistical -- the crashing frame
IS the buffer-growth branch, and only a size change reaches it.

The rate is also not stable, and that instability is the headline rather
than a footnote. The first 8 attempts gave 2 and the next 28 gave 1; and
later the same day, on the same machine and the SAME baseline binary,
41 further attempts gave 0. So a clean run proves nothing at any length
tried so far. Whenever this is used to judge a fix, run the unfixed
baseline INTERLEAVED with the candidate (-NativeDll, one attempt each,
alternating) and treat the whole experiment as void unless the baseline
arm actually crashed.

Every attempt is a cold process. Crashes are detected two ways -- the
process exiting under us, and a new envelope appearing in the crash
directory -- because the renderer thread can die a little after the seam
call it followed has already been answered.

Nothing here kills a Wintty it did not start, and nothing deletes an
existing crash envelope; the directory is snapshotted by name, not
cleared.

    . C:\temp\seam-lock.ps1
    $lock = Enter-SeamLock -Owner 'investigate/first-toggle-crash'
    try { ./first-toggle-crash-repro.ps1 -Arm toggle -Attempts 20 }
    finally { Exit-SeamLock $lock }

Exit codes: 0 no crash in -Attempts tries, 1 reproduced (a finding),
2 the harness could not run.
#>
param(
    [ValidateSet('toggle', 'idle', 'bare', 'resize')][string]$Arm = 'toggle',
    [int]$Attempts = 20,
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),

    # How long to keep watching after the arm's action. The renderer thread
    # runs its own loop, so a crash the toggle caused can land after the UI
    # thread has already answered the seam. Measured settle for a cold first
    # switch is 700-1500ms; 4s leaves room for a slow one plus the sentry
    # backend writing its envelope.
    [int]$DwellMs = 4000,

    # Stop at the first crash. Off by default: a hit rate over a stated
    # number of attempts is the finding, and one hit is not a rate.
    [switch]$StopOnFirst,

    # libghostty to test. Copied over the deployed native\ghostty.dll
    # before every attempt, so a caller can alternate two builds inside
    # one session and one machine load. That interleaving is the point:
    # this crash's rate drifts enough that a fixed build measured on
    # Monday against a baseline measured on Tuesday proves nothing.
    [string]$NativeDll = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')

$crashDir = Join-Path $env:LOCALAPPDATA 'Wintty\crash'
$symbolizer = 'C:\Program Files\LLVM\bin\llvm-symbolizer.exe'

if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: no build at $ExePath"
    exit 2
}
try { Assert-NoWintty -Context 'first-toggle-crash-repro' }
catch { Write-Host "HARNESS: $($_.Exception.Message)"; exit 2 }

$config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@

function Get-CrashNames {
    if (-not (Test-Path $crashDir)) { return @() }
    return @(Get-ChildItem $crashDir -Filter '*.winttycrash' | ForEach-Object { $_.Name })
}

# Turn one envelope into a module+offset chain, and put source lines on the
# ghostty.dll frames when a pdb sits beside the deployed dll. Without this
# the report is 29 bare addresses, which is what made the original sighting
# unactionable.
function Show-CrashEnvelope([string]$Path, [string]$Obj) {
    $payload = (([System.IO.File]::ReadAllText($Path)) -split "`n") |
        Where-Object { $_ -like '*"exception"*' } | Select-Object -First 1
    if (-not $payload) { Write-Host "    (envelope has no exception payload)"; return }
    $j = $payload | ConvertFrom-Json
    $imgs = @($j.debug_meta.images | ForEach-Object {
        [pscustomobject]@{
            Name = ($_.code_file -replace '.*/', '')
            Base = [uint64]$_.image_addr
            Size = [uint64]$_.image_size
        }
    })
    Write-Host ("    type={0} thread={1} release={2}" -f
        $j.exception.values[0].type, $j.tags.'thread-type', $j.release)
    $i = 0
    foreach ($fr in $j.exception.values[0].stacktrace.frames) {
        $addr = [uint64]$fr.instruction_addr
        $hit = $imgs | Where-Object { $addr -ge $_.Base -and $addr -lt ($_.Base + $_.Size) } | Select-Object -First 1
        if (-not $hit) { Write-Host ("    {0,2}  {1}  ???" -f $i, $fr.instruction_addr); $i++; continue }
        $rva = $addr - $hit.Base
        $line = "    {0,2}  {1}+0x{2:x}" -f $i, $hit.Name, $rva
        if ($hit.Name -eq 'ghostty.dll' -and (Test-Path $symbolizer) -and (Test-Path $Obj)) {
            # -1: the frame holds a RETURN address, so the call site is the
            # byte before it. Symbolizing the raw value lands on the next
            # statement and blames the wrong line.
            $sym = & $symbolizer --obj=$Obj --relative-address ("0x{0:x}" -f ($rva - 1)) 2>$null
            $named = @($sym | Where-Object { $_ -ne '' })
            if ($named.Count -ge 2) { $line += "  {0}  {1}" -f $named[0], $named[1] }
        }
        Write-Host $line
        $i++
    }
}

$deployedDll = Join-Path (Split-Path -Parent (Resolve-Path $ExePath).Path) 'native\ghostty.dll'
$tabs = @('tab-1', 'tab-2', 'tab-3', 'tab-4', 'tab-5')
$hits = @()
$ran = 0

Write-Host ("arm={0} attempts={1} dwell={2}ms exe={3}" -f $Arm, $Attempts, $DwellMs, $ExePath)

if ($NativeDll -and -not (Test-Path $NativeDll)) {
    Write-Host "HARNESS: no libghostty at $NativeDll"
    exit 2
}

for ($n = 1; $n -le $Attempts; $n++) {
    if ($NativeDll) {
        # Swap between attempts, never during one: the app has the dll
        # mapped for its whole life, so this is only safe with no Wintty
        # running, which is exactly where each attempt begins.
        Copy-Item $NativeDll $deployedDll -Force
        # Carry the matching pdb across too. Symbolizing a fixed build
        # against the baseline's pdb would silently blame the wrong lines,
        # which is worse than not symbolizing at all.
        $srcPdb = Join-Path (Split-Path -Parent $NativeDll) 'ghostty.pdb'
        if (Test-Path $srcPdb) {
            Copy-Item $srcPdb (Join-Path (Split-Path -Parent $deployedDll) 'ghostty.pdb') -Force
        }
    }
    $before = Get-CrashNames
    $session = $null
    $verdict = 'clean'
    $exitCode = $null
    $where = ''
    try {
        $session = Start-SeamSession -ExePath $ExePath -ConfigText $config

        if ($Arm -ne 'bare') {
            $where = 'seed'
            Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = $tabs.Count; titles = $tabs } | Out-Null
            Invoke-SeamCommand $session @{ op = 'pin'; index = 1; via = 'router' } | Out-Null
            Invoke-SeamCommand $session @{ op = 'group'; indices = @(2, 3) } | Out-Null
            Invoke-SeamCommand $session @{ op = 'select'; index = 4 } | Out-Null
            Invoke-SeamCommand $session @{ op = 'collapse'; index = 2; collapsed = $true; via = 'router' } | Out-Null
        }
        if ($Arm -eq 'toggle') {
            $where = 'toggle'
            Invoke-SeamCommand $session @{ op = 'toggle-layout' } | Out-Null
        }
        if ($Arm -eq 'resize') {
            # Shrink first so the grow that follows really is a grow: the
            # release path only runs when the cell count OUTGROWS the
            # buffer, and a buffer sized for the launch geometry would
            # otherwise swallow anything smaller than the screen.
            $where = 'shrink'
            $launched = [SeamWin]::RectOf($session.Hwnd64)
            [void][SeamWin]::MoveWindow([SeamWin]::P($session.Hwnd64), 80, 80, 900, 620, $true)
            Start-Sleep -Milliseconds 1200
            $small = [SeamWin]::RectOf($session.Hwnd64)
            $where = 'grow'
            [void][SeamWin]::MoveWindow([SeamWin]::P($session.Hwnd64), 80, 80, 1700, 1050, $true)
            Start-Sleep -Milliseconds 600
            $big = [SeamWin]::RectOf($session.Hwnd64)
            # A control arm nobody checked is not a control. Say what the
            # window actually did, so a clean result cannot be read as
            # "growth is safe" when the growth never happened.
            Write-Host ("    geometry: launch {0}x{1} -> small {2}x{3} -> big {4}x{5}" -f
                $launched.W, $launched.Hh, $small.W, $small.Hh, $big.W, $big.Hh)
            if ($big.W -le $small.W -or $big.Hh -le $small.Hh) {
                Write-Host '    HARNESS: the grow did not grow; this arm proves nothing'
            }
        }

        # Watch, do not sleep: the exit code is the evidence and it is only
        # readable while the Process object is alive.
        $where = 'dwell'
        $deadline = [datetime]::UtcNow.AddMilliseconds($DwellMs)
        while ([datetime]::UtcNow -lt $deadline) {
            $session.Proc.Refresh()
            if ($session.Proc.HasExited) { break }
            Start-Sleep -Milliseconds 100
        }
        $session.Proc.Refresh()
        if ($session.Proc.HasExited) {
            $verdict = 'crash'
            $exitCode = $session.Proc.ExitCode
        }
    }
    catch {
        $msg = $_.Exception.Message
        if ($msg -like 'PRODUCT_EXIT*' -or $msg -like 'PRODUCT_FAIL*' -or $msg -like '*app exited*') {
            $verdict = 'crash'
            if ($msg -match 'code (-?\d+)|exit=(-?\d+)') { $exitCode = [int]($Matches[1] ?? $Matches[2]) }
        }
        else {
            # The finally below still runs on the way out, so the session
            # is torn down exactly once whichever way this attempt ends.
            Write-Host ("  attempt {0}: HARNESS {1}" -f $n, $msg)
            exit 2
        }
    }
    finally {
        if ($session) { Stop-SeamSession $session }
    }
    $ran++

    # Envelope detection LAGS BY ONE ATTEMPT and cannot be relied on for
    # the count. sentry writes a crash envelope on the NEXT launch, not at
    # crash time -- gpu.log shows "processing and pruning old runs" then
    # "sending envelope" during startup -- so the file for attempt N
    # usually appears while attempt N+1 is booting. The process-exit check
    # above is the trustworthy detector; this poll only enriches a crash
    # that was already caught, and may attribute a stack to its successor.
    # Reconcile totals against the envelope COUNT for the whole run, never
    # per attempt.
    $new = @()
    $envDeadline = [datetime]::UtcNow.AddSeconds(6)
    while ([datetime]::UtcNow -lt $envDeadline) {
        $new = @(Get-CrashNames | Where-Object { $_ -notin $before })
        if ($new.Count -gt 0) { break }
        if ($verdict -ne 'crash') { break }
        Start-Sleep -Milliseconds 400
    }
    if ($new.Count -gt 0) { $verdict = 'crash' }

    if ($verdict -eq 'crash') {
        $hits += [pscustomobject]@{ Attempt = $n; ExitCode = $exitCode; Where = $where; Envelope = ($new -join ',') }
        Write-Host ("  attempt {0}: CRASH at '{1}' exit={2} envelope={3}" -f $n, $where, $exitCode, ($new -join ','))
        foreach ($e in $new) { Show-CrashEnvelope (Join-Path $crashDir $e) $deployedDll }
        if ($StopOnFirst) { break }
    }
    else {
        Write-Host ("  attempt {0}: clean" -f $n)
    }
}

Write-Host ''
Write-Host ("RESULT arm={0}: {1} of {2} attempts crashed" -f $Arm, $hits.Count, $ran)
foreach ($h in $hits) {
    Write-Host ("  attempt {0} at '{1}' exit={2}" -f $h.Attempt, $h.Where, $h.ExitCode)
}
if ($hits.Count -gt 0) { exit 1 }
exit 0
