#requires -Version 7
# Tests for lib/wintty-process.ps1: the coexistence guard, the narrow
# same-exe gate, and the sweep that stops only what a run started.
#
# Same shape as EsctestParse.Tests.ps1: plain asserts, no Pester, run it
# directly (exit 0 green, 1 red):
#
#     pwsh -NoProfile -File windows/scripts/WinttyProcess.Tests.ps1
#
# Three layers:
#
#   1. decision cases against injected instances, environment and
#      registrations: every rule of the guard, both ways, plus the sweep
#      against stand-ins that record a Kill instead of taking one;
#   2. live cases against real processes: a stand-in "user" instance (a copy
#      of ping.exe named Wintty.exe, running from its own directory) must not
#      block an isolated launch, must still block one that is not isolated,
#      and is never signalled by the guard or by the sweep;
#   3. mutation rows: layer 1 again, against a copy of the library with one
#      rule broken. Every row must turn it red; a row that stays green means
#      the cases no longer pin that rule.
#
# Nothing here launches Wintty. The stand-ins are ping.exe copies, and they
# are stopped by exact pid in a finally.

$ErrorActionPreference = 'Stop'
# The stand-ins are started with Start-Process, and the config isolation
# scan reads every launch under windows/scripts as a possible app launch.
# They are ping.exe, not the app; arming the guard costs nothing.
$env:WINTTY_TEST_CONFIG = '1'

$script:fails = 0
function Assert-True($cond, $msg) {
    if (-not $cond) { $script:fails++; Write-Host "FAIL: $msg" -ForegroundColor Red }
    else { Write-Host "ok: $msg" }
}

$script:lib = Join-Path $PSScriptRoot 'lib/wintty-process.ps1'

# ---- layer 1: decisions -------------------------------------------------------

# Runs every decision case against the library at $LibPath and returns the
# names of the cases that went wrong. Dot-sourced into a child scope, so a
# mutant copy never leaks its functions into the next row.
function Invoke-DecisionCases([string]$LibPath) {
    return & {
        param($lib)
        . $lib
        $failed = [System.Collections.Generic.List[string]]::new()
        $temp = [System.IO.Path]::GetTempPath()
        $exe = 'C:\builds\mine\out\Wintty.exe'
        $userPath = 'C:\Users\someone\AppData\Local\Vendor\Wintty.User\current\Wintty.exe'
        $user = [pscustomobject]@{ Id = 101; Path = $userPath }
        $isolatedEnv = @{
            WINTTY_TEST_CONFIG = '1'
            XDG_CONFIG_HOME    = (Join-Path $temp 'wintty-seam-case')
            WINTTY_STATE_BASE  = (Join-Path $temp 'wintty-seam-case\state')
        }
        $isolatedConfig = "window-save-state = never`nwindows-single-instance = false`n"
        $userRegistrations = @{ 'Vendor.Wintty.User' = $userPath }

        function EnvWith([hashtable]$Changes) {
            $e = $isolatedEnv.Clone()
            foreach ($k in $Changes.Keys) {
                if ($null -eq $Changes[$k]) { $e.Remove($k) } else { $e[$k] = $Changes[$k] }
            }
            return $e
        }

        function Case([string]$Name, [bool]$WantAllowed, [hashtable]$Over = @{}) {
            $a = @{
                ExePath       = $exe
                ConfigText    = $isolatedConfig
                AumId         = 'com.example.mine'
                Environment   = $isolatedEnv.Clone()
                Instances     = @($user)
                Registrations = $userRegistrations
                TempRoot      = $temp
            }
            foreach ($k in $Over.Keys) { $a[$k] = $Over[$k] }
            try {
                $v = Test-WinttyCoexistence @a
                if ($v.Allowed -ne $WantAllowed) {
                    $failed.Add("$Name (allowed=$($v.Allowed): $($v.Reasons -join ' | '))")
                }
            }
            catch { $failed.Add("$Name (threw: $($_.Exception.Message))") }
        }

        # Nothing running: allowed however unisolated (every harness's old path).
        Case 'alone, unisolated' $true @{ Instances = @(); ConfigText = ''; Environment = @{} }
        # The point of the guard.
        Case 'isolated, beside another edition' $true
        Case 'same edition' $false @{ Registrations = @{ 'com.example.mine' = $userPath } }
        Case 'same edition, AUMID in another case' $false @{
            AumId = 'COM.Example.MINE'; Registrations = @{ 'com.example.mine' = $userPath } }
        Case 'same edition among several registrations' $false @{
            Registrations = @{ 'Vendor.Wintty.User' = $userPath; 'com.example.mine' = $userPath } }
        Case 'running instance holds no registration' $false @{ Registrations = @{} }
        Case 'registration names another exe' $false @{
            Registrations = @{ 'Vendor.Wintty.User' = 'C:\elsewhere\Wintty.exe' } }
        Case 'build AUMID unknown (no Ghostty.Core.dll)' $false @{ AumId = '' }
        Case 'an instance of this exe is running' $false @{
            Instances = @([pscustomobject]@{ Id = 7; Path = $exe }) }
        Case 'an instance of this exe, other spelling' $false @{
            Instances = @($user, [pscustomobject]@{ Id = 7; Path = 'c:/BUILDS/mine/out/wintty.exe' }) }
        Case 'an instance whose path cannot be read' $false @{
            Instances = @($user, [pscustomobject]@{ Id = 8; Path = $null }) }
        Case 'exe inside the running install' $false @{
            ExePath = 'C:\Users\someone\AppData\Local\Vendor\Wintty.User\app-2\Wintty.exe' }
        Case 'single-instance left at its default' $false @{ ConfigText = 'window-save-state = never' }
        Case 'single-instance on' $false @{ ConfigText = 'windows-single-instance = true' }
        Case 'single-instance: the last line wins (on)' $false @{
            ConfigText = "windows-single-instance = false`nwindows-single-instance = true" }
        Case 'single-instance: the last line wins (off)' $true @{
            ConfigText = "windows-single-instance = true`r`nwindows-single-instance = false" }
        Case 'test marker missing' $false @{ Environment = (EnvWith @{ WINTTY_TEST_CONFIG = $null }) }
        Case 'test marker not 1' $false @{ Environment = (EnvWith @{ WINTTY_TEST_CONFIG = 'yes' }) }
        Case 'XDG_CONFIG_HOME missing' $false @{ Environment = (EnvWith @{ XDG_CONFIG_HOME = $null }) }
        Case 'XDG_CONFIG_HOME outside temp' $false @{
            Environment = (EnvWith @{ XDG_CONFIG_HOME = 'C:\Users\someone\AppData\Roaming' }) }
        Case 'state base missing' $false @{ Environment = (EnvWith @{ WINTTY_STATE_BASE = $null }) }
        Case 'state base outside temp' $false @{
            Environment = (EnvWith @{ WINTTY_STATE_BASE = 'C:\Users\someone\AppData\Local' }) }
        Case 'per-user daemon pipe' $false @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_PIPE = '\\.\pipe\winttyd-Vendor.Wintty.User-S-1-5-21-11-22-33-1001' }) }
        Case 'per-user daemon pipe, versioned' $false @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_PIPE = '\\.\pipe\winttyd-S-1-5-21-11-22-33-1001-v1.0.0' }) }
        Case 'private daemon pipe' $true @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_PIPE = '\\.\pipe\winttyd-test-0123456789abcdef0123456789abcdef' }) }
        Case 'daemon data dir outside temp' $false @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_DATA_DIR = 'C:\Users\someone\AppData\Local\Vendor\sessiond' }) }
        Case 'daemon log file under temp' $true @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_LOG_FILE = (Join-Path $temp 'wintty-seam-case\d\log.txt') }) }

        # The guard reads; it never stops, signals or closes anything.
        foreach ($name in 'Test-WinttyCoexistence', 'Assert-WinttyCoexistence', 'Assert-NoWinttyFrom',
                'Get-WinttyInstances', 'Get-WinttyToastRegistrations') {
            $body = (Get-Command $name).ScriptBlock.ToString()
            if ($body -match '(?i)Stop-Process|\.Kill\(|taskkill|CloseMainWindow|PostMessage') {
                $failed.Add("$name signals a process")
            }
        }

        # The sweep, against stand-ins that record a Kill rather than take one.
        $sweepRoot = Join-Path $temp "wintty-sweep-case-$PID"
        New-Item -ItemType Directory -Force -Path $sweepRoot | Out-Null
        $mineImage = Join-Path $sweepRoot 'Wintty.exe'
        if (-not (Test-Path $mineImage)) { [System.IO.File]::WriteAllBytes($mineImage, [byte[]]@()) }
        $killed = [System.Collections.Generic.List[int]]::new()
        $since = Get-Date
        # The record rides on the object itself: a closure over $killed would
        # capture only the scope it is made in, and a Kill that throws is
        # swallowed by the sweep, which would make this case vacuous.
        $standIn = {
            param([int]$Id, $Path, $Started)
            $o = [pscustomobject]@{ Id = $Id; Path = $Path; StartTime = $Started; Record = $killed }
            $o | Add-Member ScriptMethod Kill { param($tree) $this.Record.Add($this.Id) }
            $o | Add-Member ScriptMethod WaitForExit { param($ms) $true }
            return $o
        }
        $stand = @(
            (& $standIn 1 $mineImage $since.AddSeconds(5)),                  # ours: stopped
            (& $standIn 2 $mineImage.ToUpperInvariant() $since.AddSeconds(5)), # ours, other spelling: stopped
            (& $standIn 3 $userPath $since.AddSeconds(5)),                   # another exe: left alone
            (& $standIn 4 $mineImage $since.AddSeconds(-60)),                # ours but older: left alone
            (& $standIn 5 $null $since.AddSeconds(5))                        # unreadable: left alone
        )
        try {
            Stop-WinttyStartedAfter -Since $since -ExePath $mineImage -Processes $stand
            if ((@($killed | Sort-Object) -join ',') -ne '1,2') {
                $failed.Add("sweep stopped [$($killed -join ',')], wanted [1,2]")
            }
        }
        catch { $failed.Add("sweep threw: $($_.Exception.Message)") }
        finally { Remove-Item -LiteralPath $sweepRoot -Recurse -Force -ErrorAction SilentlyContinue }

        return , $failed
    } $LibPath
}

$decisionFailures = Invoke-DecisionCases $script:lib
foreach ($f in $decisionFailures) { Assert-True $false "decision: $f" }
Assert-True ($decisionFailures.Count -eq 0) 'every decision case holds against the real library'

# ---- layer 2: live processes ----------------------------------------------------

. $script:lib

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-coexist-" + [guid]::NewGuid().ToString('N'))
$userDir = Join-Path $root 'user\app'
$mineDir = Join-Path $root 'mine\app'
New-Item -ItemType Directory -Force -Path $userDir, $mineDir, (Join-Path $root 'cfg'), (Join-Path $root 'state') | Out-Null
$userImage = Join-Path $userDir 'Wintty.exe'
$mineImage = Join-Path $mineDir 'Wintty.exe'
Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $userImage
Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $mineImage

# A Ghostty.Core.dll beside the "mine" image carrying the one constant the
# guard reads, so the AUMID really comes out of assembly metadata here.
Add-Type -OutputAssembly (Join-Path $mineDir 'Ghostty.Core.dll') -OutputType Library -TypeDefinition @'
namespace Ghostty.Core.Version { public static class BuildInfo { public const string AumId = "com.example.fixture"; } }
'@

$isolated = @{
    WINTTY_TEST_CONFIG = '1'
    XDG_CONFIG_HOME    = (Join-Path $root 'cfg')
    WINTTY_STATE_BASE  = (Join-Path $root 'state')
    WINTTY_SESSIOND_PIPE = '\\.\pipe\winttyd-test-' + [guid]::NewGuid().ToString('N')
}
$config = "windows-single-instance = false`n"
# Live process table, narrowed to this run's stand-ins: the cases must not
# depend on which real instances happen to be open on the machine (the live
# end-to-end run beside a real one is a separate check).
$standIns = { @(Get-WinttyInstances | Where-Object { $_.Path -and (ConvertTo-WinttyPathKey $_.Path).StartsWith((ConvertTo-WinttyPathKey $root) + '\') }) }
$userRegistration = @{ 'com.example.user' = $userImage }

$userProc = $null; $mineProc = $null
try {
    $userProc = Start-Process -FilePath $userImage -ArgumentList '-n', '120', '127.0.0.1' -WindowStyle Hidden -PassThru
    $userStarted = $userProc.StartTime
    Start-Sleep -Milliseconds 300

    Assert-True ((Get-WinttyBuildAumid $mineImage) -ceq 'com.example.fixture') 'the build AUMID is read out of Ghostty.Core.dll metadata'

    $v = Test-WinttyCoexistence -ExePath $mineImage -ConfigText $config -Environment $isolated `
        -Instances (& $standIns) -Registrations $userRegistration
    Assert-True ($v.Allowed -and @($v.Running).Count -eq 1) "an isolated launch runs beside a user instance from another exe ($($v.Reasons -join ' | '))"

    $v = Test-WinttyCoexistence -ExePath $mineImage -ConfigText 'windows-single-instance = true' -Environment $isolated `
        -Instances (& $standIns) -Registrations $userRegistration
    Assert-True (-not $v.Allowed) 'a launch with single-instance on still refuses beside it'

    $noState = $isolated.Clone(); $noState.Remove('WINTTY_STATE_BASE')
    $v = Test-WinttyCoexistence -ExePath $mineImage -ConfigText $config -Environment $noState `
        -Instances (& $standIns) -Registrations $userRegistration
    Assert-True (-not $v.Allowed) 'a launch on the real state base still refuses beside it'

    $v = Test-WinttyCoexistence -ExePath $mineImage -ConfigText $config -Environment $isolated `
        -Instances (& $standIns) -Registrations @{ 'com.example.fixture' = $userImage }
    Assert-True (-not $v.Allowed) 'a launch of the same edition (AUMID from the dll) refuses beside it'

    $threw = $false
    try { Assert-WinttyCoexistence -ExePath $mineImage -ConfigText 'windows-single-instance = true' | Out-Null }
    catch { $threw = $_.Exception.Message -like '*Nothing was launched and nothing was stopped*' }
    Assert-True $threw 'Assert-WinttyCoexistence refuses a non-isolated launch with the reasons (live table, live environment)'

    $threw = $false
    try { Assert-NoWinttyFrom -ExePath $mineImage }
    catch { $threw = $true }
    Assert-True (-not $threw) 'Assert-NoWinttyFrom lets a harness start beside an instance of another exe'

    $mineStamp = Get-Date
    $mineProc = Start-Process -FilePath $mineImage -ArgumentList '-n', '120', '127.0.0.1' -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 300

    $threw = $false
    try { Assert-NoWinttyFrom -ExePath $mineImage }
    catch { $threw = $true }
    Assert-True $threw 'Assert-NoWinttyFrom refuses an instance of the exe under test'

    $v = Test-WinttyCoexistence -ExePath $mineImage -ConfigText $config -Environment $isolated `
        -Instances (& $standIns) -Registrations $userRegistration
    Assert-True (-not $v.Allowed) 'an isolated launch refuses while an instance of its own exe runs'

    # The sweep against the live table: ours goes, the user's stays, even
    # with a -Since that predates the user's start.
    Stop-WinttyStartedAfter -Since $userStarted.AddSeconds(-5) -ExePath $mineImage
    Start-Sleep -Milliseconds 300
    $mineProc.Refresh()
    Assert-True $mineProc.HasExited 'the sweep stops the instance of its own exe'

    $userProc.Refresh()
    $still = Get-Process -Id $userProc.Id -ErrorAction SilentlyContinue
    Assert-True ((-not $userProc.HasExited) -and $still -and $still.StartTime -eq $userStarted) 'the user instance is untouched: same pid, same start time, never signalled'
}
finally {
    foreach ($p in @($mineProc, $userProc)) {
        if ($p -and -not $p.HasExited) { try { $p.Kill(); [void]$p.WaitForExit(3000) } catch { } }
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- layer 3: mutation rows --------------------------------------------------------

$source = Get-Content -LiteralPath $script:lib -Raw
$mutations = @(
    @{ Name = 'refuse whenever anything runs (the old Assert-NoWintty rule)'
       Find = 'if ($running.Count -eq 0) { return $verdict }'
       Replace = 'if ($running.Count -eq 0) { return $verdict }; $why.Add(''something runs'')' },
    @{ Name = 'no early allow when alone'
       Find = 'if ($running.Count -eq 0) { return $verdict }'; Replace = '' },
    @{ Name = 'single-instance not checked'
       Find = 'if ($single -cne ''false'') {'; Replace = 'if ($false) {' },
    @{ Name = 'same edition allowed'
       Find = 'elseif (@($owned | Where-Object { $_ -ieq $AumId }).Count -gt 0) {'; Replace = 'elseif ($false) {' },
    @{ Name = 'AUMID compared case-sensitively'
       Find = 'Where-Object { $_ -ieq $AumId }'; Replace = 'Where-Object { $_ -ceq $AumId }' },
    @{ Name = 'an unregistered instance is trusted'
       Find = 'if ($owned.Count -eq 0) {'; Replace = 'if ($false) {' },
    @{ Name = 'an unknown build AUMID is trusted'
       Find = '$why.Add("the AUMID this build runs as cannot be read'
       Replace = '$null = ("the AUMID this build runs as cannot be read' },
    @{ Name = 'an instance of this exe is ignored'
       Find = 'foreach ($p in $own) {'; Replace = 'foreach ($p in @()) {' },
    @{ Name = 'an unreadable instance is ignored'
       Find = 'foreach ($p in @($running | Where-Object { [string]::IsNullOrWhiteSpace($_.Path) })) {'
       Replace = 'foreach ($p in @()) {' },
    @{ Name = 'the installed app may be launched'
       Find = 'if ($installRoot -and @($mine'; Replace = 'if ($false -and @($mine' },
    @{ Name = 'test marker not checked'
       Find = "if ((& `$read 'WINTTY_TEST_CONFIG') -cne '1') {"; Replace = 'if ($false) {' },
    @{ Name = 'XDG_CONFIG_HOME may sit outside temp'
       Find = 'elseif (-not (Test-WinttyPathUnder $xdg $TempRoot))'; Replace = 'elseif ($false)' },
    @{ Name = 'the state base check reads another variable'
       Find = "`$stateBase = & `$read 'WINTTY_STATE_BASE'"; Replace = "`$stateBase = & `$read 'XDG_CONFIG_HOME'" },
    @{ Name = 'state base may sit outside temp'
       Find = 'elseif (-not (Test-WinttyPathUnder $stateBase $TempRoot))'; Replace = 'elseif ($false)' },
    @{ Name = 'a per-user daemon pipe is accepted'
       Find = "-match '(?i)-S-1-\d+(-\d+)+'"; Replace = "-match 'never(?!)'" },
    @{ Name = 'daemon dirs may sit outside temp'
       Find = 'if ($value -and -not (Test-WinttyPathUnder $value $TempRoot)) {'; Replace = 'if ($false) {' },
    @{ Name = 'the sweep ignores the exe path'
       Find = '$spellings -notcontains (ConvertTo-WinttyPathKey $path)) { continue }'; Replace = '$false) { continue }' },
    @{ Name = 'the sweep ignores the start time'
       Find = '$started -lt $Since) { continue }'; Replace = '$false) { continue }' },
    @{ Name = 'the guard stops what it refuses'
       Find = '$verdict.Allowed = $why.Count -eq 0'
       Replace = 'foreach ($x in $foreign) { Stop-Process -Id $x.Id -WhatIf }; $verdict.Allowed = $why.Count -eq 0' }
)
$mutantDir = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-process-mutants-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $mutantDir | Out-Null
try {
    $i = 0
    foreach ($m in $mutations) {
        $i++
        if (-not $source.Contains($m.Find)) {
            Assert-True $false "mutation '$($m.Name)': its anchor is gone from the library"
            continue
        }
        $mutant = Join-Path $mutantDir "mutant-$i.ps1"
        Set-Content -LiteralPath $mutant -Value $source.Replace($m.Find, $m.Replace) -NoNewline -Encoding utf8
        $red = Invoke-DecisionCases $mutant
        Assert-True ($red.Count -gt 0) "mutation '$($m.Name)' turns the cases red ($($red.Count): $(@($red)[0]))"
    }
}
finally { Remove-Item -LiteralPath $mutantDir -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
if ($script:fails -gt 0) { Write-Host "$script:fails failure(s)" -ForegroundColor Red; exit 1 }
Write-Host 'all green'
exit 0
