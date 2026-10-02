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

# A build whose Ghostty.Core.dll carries an AUMID, for the cases where the
# build's own AUMID and a passed one meet. Compiled once, never loaded.
$script:aumFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-aum-fixture-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $script:aumFixtureRoot 'app') | Out-Null
$script:aumFixtureExe = Join-Path $script:aumFixtureRoot 'app\Wintty.exe'
Add-Type -OutputAssembly (Join-Path $script:aumFixtureRoot 'app\Ghostty.Core.dll') -OutputType Library -TypeDefinition @"
namespace Ghostty.Core.Version { public static class BuildInfo { public const string AumId = "com.example.built"; } }
"@
# ---- layer 1: decisions -------------------------------------------------------

# Runs every decision case against the library at $LibPath and returns the
# names of the cases that went wrong. Dot-sourced into a child scope, so a
# mutant copy never leaks its functions into the next row.
function Invoke-DecisionCases([string]$LibPath) {
    return & {
        param($lib)
        . $lib
        $failed = [System.Collections.Generic.List[string]]::new()
        foreach ($name in 'New-WinttyOwnedStateBase', 'Test-WinttyOwnedStateBase', 'Test-WinttyCoexistencePreflight', 'Write-WinttyTimingNeighbourWarning',
                'Assert-WinttySessionStateFree', 'Invoke-WinttySessionStateChange', 'Assert-WinttyCaptureClear') {
            if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
                $failed.Add("$name does not exist")
                return , $failed
            }
        }
        $temp = [System.IO.Path]::GetTempPath()
        $exe = 'C:\builds\mine\out\Wintty.exe'
        $userPath = 'C:\Users\someone\AppData\Local\Vendor\Wintty.User\current\Wintty.exe'
        $user = [pscustomobject]@{ Id = 101; Path = $userPath }
        $isolatedEnv = @{
            WINTTY_TEST_CONFIG = '1'
            XDG_CONFIG_HOME    = (Join-Path $temp 'wintty-seam-case')
            WINTTY_STATE_BASE  = (Join-Path $temp 'wintty-seam-case\state')
            XDG_STATE_HOME     = (Join-Path $temp 'wintty-seam-case\state\xdg-state')
            XDG_CACHE_HOME     = (Join-Path $temp 'wintty-seam-case\state\xdg-cache')
        }
        $chord = Get-WinttyHarnessQuickTerminalKey
        $isolatedConfig = "window-save-state = never`nwindows-single-instance = false`nquick-terminal-key = $chord`n"
        $userRegistrations = @{ 'Vendor.Wintty.User' = $userPath }
        # A Velopack install nobody runs: Update.exe in the install root,
        # the app in current\ (the exe itself need not exist).
        $vpkRoot = Join-Path $temp "wintty-vpk-case-$PID"
        New-Item -ItemType Directory -Force -Path $vpkRoot | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $vpkRoot 'Update.exe'), [byte[]]@())
        # A sibling of the temp directory whose name starts with temp's own.
        $tempSibling = $temp.TrimEnd('\') + '-evil'

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
        # The build's own AUMID wins; a passed one only fills in where the
        # build's cannot be read, and one that contradicts it refuses.
        Case 'AUMID read off the build itself' $true @{ ExePath = $script:aumFixtureExe; AumId = '' }
        Case 'passed AUMID agrees with the build''s own' $true @{ ExePath = $script:aumFixtureExe; AumId = 'COM.example.built' }
        Case 'passed AUMID contradicts the build''s own' $false @{ ExePath = $script:aumFixtureExe; AumId = 'com.example.mine' }
        Case 'passed AUMID cannot hide a same-edition build' $false @{
            ExePath = $script:aumFixtureExe; AumId = 'com.example.mine'; Registrations = @{ 'com.example.built' = $userPath } }

        # A segment whose attributes cannot be read fails closed; one that is
        # not there yet is skipped.
        $denied = { param($p) throw [System.UnauthorizedAccessException]::new('denied') }
        $absent = { param($p) throw [System.IO.FileNotFoundException]::new('absent') }
        if (Test-WinttyPathUnder (Join-Path $temp 'wintty-attr-case\x') $temp -ReadAttributes $denied) {
            $failed.Add('a segment whose attributes cannot be read counts as under temp')
        }
        if (-not (Test-WinttyPathUnder (Join-Path $temp 'wintty-attr-case\x') $temp -ReadAttributes $absent)) {
            $failed.Add('a segment that does not exist yet stops a path counting as under temp')
        }

        # The capture gate: every probed point of the sampled rect must be the
        # harness window's, and one that is not refuses the sample.
        $fakeHwnd = [int64]0x7F00AA
        $threw = $false
        try { Assert-WinttyCaptureClear -Hwnd64 $fakeHwnd -X 10 -Y 20 -Width 300 -Height 200 -RootAt { param($x, $y) $fakeHwnd } } catch { $threw = $true }
        if ($threw) { $failed.Add('the capture gate refuses a rect its own window covers entirely') }
        $threw = $false
        try {
            Assert-WinttyCaptureClear -Hwnd64 $fakeHwnd -X 10 -Y 20 -Width 300 -Height 200 `
                -RootAt { param($x, $y) if ($x -eq 160 -and $y -eq 120) { [int64]0x7F00BB } else { $fakeHwnd } }
        }
        catch { $threw = "$_" -match '^HARVEST_MISS' }
        if (-not $threw) { $failed.Add('the capture gate takes a sample whose centre another window covers') }
        # The session-state helpers: refuse beside another Wintty before
        # anything changes, skip when asked, and always restore once the
        # change ran.
        $ran = @{ change = 0; restore = 0 }
        $threw = $false
        try { Assert-WinttySessionStateFree -ExePath $exe -What 'x' -Instances @($user) } catch { $threw = "$_" -match 'skipped' }
        if (-not $threw) { $failed.Add('Assert-WinttySessionStateFree lets a change go ahead beside another Wintty') }
        $threw = $false
        try { Assert-WinttySessionStateFree -ExePath $exe -What 'x' -Instances @([pscustomobject]@{ Id = 7; Path = $exe }) } catch { $threw = $true }
        if ($threw) { $failed.Add('Assert-WinttySessionStateFree refuses over the run''s own exe') }
        $threw = $false
        try { [void](Invoke-WinttySessionStateChange -ExePath $exe -What 'x' -Instances @($user) -Change { $ran.change++ } -Restore { $ran.restore++ }) } catch { $threw = $true }
        if (-not $threw -or $ran.change -ne 0 -or $ran.restore -ne 0) { $failed.Add('Invoke-WinttySessionStateChange ran a change beside another Wintty') }
        $r = Invoke-WinttySessionStateChange -ExePath $exe -What 'x' -Instances @($user) -SkipBeside -Change { $ran.change++ } -Restore { $ran.restore++ }
        if ($r -ne $false -or $ran.change -ne 0) { $failed.Add('-SkipBeside did not skip the change beside another Wintty') }
        try { [void](Invoke-WinttySessionStateChange -ExePath $exe -What 'x' -Instances @() -Change { $ran.change++; throw 'mid-change' } -Restore { $ran.restore++ }) } catch { }
        if ($ran.change -ne 1 -or $ran.restore -ne 1) { $failed.Add('Invoke-WinttySessionStateChange did not restore after a change that threw') }
        Case 'an instance of this exe is running' $false @{
            Instances = @([pscustomobject]@{ Id = 7; Path = $exe }) }
        Case 'an instance of this exe, other spelling' $false @{
            Instances = @($user, [pscustomobject]@{ Id = 7; Path = 'c:/BUILDS/mine/out/wintty.exe' }) }
        Case 'an instance whose path cannot be read' $false @{
            Instances = @($user, [pscustomobject]@{ Id = 8; Path = $null }) }
        Case 'exe inside the running install' $false @{
            ExePath = 'C:\Users\someone\AppData\Local\Vendor\Wintty.User\app-2\Wintty.exe' }
        Case 'exe in a Velopack install nobody runs' $false @{ ExePath = (Join-Path $vpkRoot 'current\Wintty.exe') }
        Case 'exe under Program Files, not running' $false @{
            ExePath = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Wintty\Wintty.exe') }
        Case 'exe under an install base' $false @{
            ExePath = 'D:\Apps\Wintty\Wintty.exe'; InstallBases = @('D:\Apps') }
        Case 'single-instance left at its default' $false @{ ConfigText = "quick-terminal-key = $chord" }
        Case 'single-instance on' $false @{ ConfigText = "windows-single-instance = true`nquick-terminal-key = $chord" }
        # The app's election reads the FIRST line; the guard requires every one.
        Case 'single-instance: a later line turns it on' $false @{
            ConfigText = "windows-single-instance = false`nwindows-single-instance = true`nquick-terminal-key = $chord" }
        Case 'single-instance: an earlier line turns it on' $false @{
            ConfigText = "windows-single-instance = true`r`nwindows-single-instance = false`nquick-terminal-key = $chord" }
        Case 'single-instance: the key in another case turns it on' $false @{
            ConfigText = "Windows-Single-Instance=true`nwindows-single-instance = false`nquick-terminal-key = $chord" }
        Case 'single-instance: a bare CR ends a line, as in the app (on)' $false @{
            ConfigText = "windows-single-instance = true`rwindows-single-instance = false`nquick-terminal-key = $chord" }
        Case 'single-instance: a bare CR ends a line, as in the app (off)' $true @{
            ConfigText = "windows-single-instance = false`rquick-terminal-key = $chord" }
        Case 'single-instance: every line off, any spelling of the key' $true @{
            ConfigText = "  WINDOWS-single-instance=false`r`n# windows-single-instance = true`nwindows-single-instance = `nwindows-single-instance = false`nquick-terminal-key = $chord" }
        Case 'quick-terminal-key left at its default' $false @{ ConfigText = 'windows-single-instance = false' }
        Case 'quick-terminal-key on the default chord' $false @{
            ConfigText = "windows-single-instance = false`nquick-terminal-key = ctrl+backquote" }
        Case 'quick-terminal-key: an earlier line binds the default chord' $false @{
            ConfigText = "windows-single-instance = false`nquick-terminal-key = ctrl+backquote`nquick-terminal-key = $chord" }
        Case 'quick-terminal-key: a later line binds another chord' $false @{
            ConfigText = "windows-single-instance = false`nquick-terminal-key = $chord`nQuick-Terminal-Key = alt+space" }
        Case 'quick-terminal-key: the harness chord in another case' $true @{
            ConfigText = "windows-single-instance = false`nQuick-Terminal-Key = $($chord.ToUpperInvariant())" }
        # What Start-SeamSession stages: the harness chord appended when the
        # config binds none, the harness's own chord kept when it does.
        Case 'a harness config with the defaults added' $true @{
            ConfigText = (Add-WinttyHarnessConfigDefaults "window-save-state = never`r`nwindows-single-instance = false`r`n") }
        Case 'an empty harness config with the defaults added still needs single-instance off' $false @{
            ConfigText = (Add-WinttyHarnessConfigDefaults '') }
        $own = "windows-single-instance = false`nquick-terminal-key = alt+space`n"
        if ((Add-WinttyHarnessConfigDefaults $own) -cne $own) {
            $failed.Add('Add-WinttyHarnessConfigDefaults changed a config that binds its own chord')
        }
        # No @() around it: the reader returns its List as one object.
        $staged = (Get-WinttyConfigValues (Add-WinttyHarnessConfigDefaults 'x = 1') 'quick-terminal-key') -join '|'
        if ($staged -cne $chord) {
            $failed.Add('Add-WinttyHarnessConfigDefaults did not stage exactly the harness chord')
        }
        Case 'test marker missing' $false @{ Environment = (EnvWith @{ WINTTY_TEST_CONFIG = $null }) }
        Case 'test marker not 1' $false @{ Environment = (EnvWith @{ WINTTY_TEST_CONFIG = 'yes' }) }
        Case 'XDG_CONFIG_HOME missing' $false @{ Environment = (EnvWith @{ XDG_CONFIG_HOME = $null }) }
        Case 'XDG_CONFIG_HOME outside temp' $false @{
            Environment = (EnvWith @{ XDG_CONFIG_HOME = 'C:\Users\someone\AppData\Roaming' }) }
        Case 'state base missing' $false @{ Environment = (EnvWith @{ WINTTY_STATE_BASE = $null }) }
        Case 'state base outside temp' $false @{
            Environment = (EnvWith @{ WINTTY_STATE_BASE = 'C:\Users\someone\AppData\Local' }) }
        # "Under temp" means under temp's directory, separator included: a
        # sibling whose name merely starts with temp's is outside.
        Case 'XDG_CONFIG_HOME in a sibling of temp' $false @{
            Environment = (EnvWith @{ XDG_CONFIG_HOME = (Join-Path $tempSibling 'cfg') }) }
        Case 'state base in a sibling of temp' $false @{
            Environment = (EnvWith @{ WINTTY_STATE_BASE = (Join-Path $tempSibling 'state') }) }
        # libghostty's native state: WINTTY_STATE_BASE does not move it.
        Case 'native state dir missing' $false @{ Environment = (EnvWith @{ XDG_STATE_HOME = $null }) }
        Case 'native state dir outside temp' $false @{
            Environment = (EnvWith @{ XDG_STATE_HOME = 'C:\Users\someone\AppData\Local' }) }
        Case 'native cache dir missing' $false @{ Environment = (EnvWith @{ XDG_CACHE_HOME = $null }) }
        Case 'native cache dir outside temp' $false @{
            Environment = (EnvWith @{ XDG_CACHE_HOME = 'C:\Users\someone\AppData\Local' }) }

        # "Under temp" is not lexical: a junction below the root that points
        # outside it fails. All of this sits under the real temp directory;
        # a stand-in root inside it plays temp, so nothing is made elsewhere.
        $jcase = Join-Path $temp "wintty-junction-case-$PID"
        $fakeTemp = Join-Path $jcase 'temp'
        $outside = Join-Path $jcase 'outside'
        New-Item -ItemType Directory -Force -Path $fakeTemp, $outside | Out-Null
        $junction = Join-Path $fakeTemp 'j'
        if (-not (Test-Path -LiteralPath $junction)) { New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null }
        if (Test-WinttyPathUnder (Join-Path $junction 'state') $fakeTemp) {
            $failed.Add('a path through a junction under the root that points outside it counts as under the root')
        }
        if (-not (Test-WinttyPathUnder (Join-Path $fakeTemp 'plain\state') $fakeTemp)) {
            $failed.Add('a plain path under the root (not yet created) does not count as under it')
        }
        $jenv = @{
            WINTTY_TEST_CONFIG = '1'
            XDG_CONFIG_HOME    = (Join-Path $fakeTemp 'cfg')
            WINTTY_STATE_BASE  = (Join-Path $junction 'state')
            XDG_STATE_HOME     = (Join-Path $fakeTemp 'xs')
            XDG_CACHE_HOME     = (Join-Path $fakeTemp 'xc')
        }
        Case 'state base through a junction out of temp' $false @{ Environment = $jenv; TempRoot = $fakeTemp }

        # Owned state trees: adopted only with the token their marker holds.
        $ownedTree = New-WinttyOwnedStateBase -Under $fakeTemp
        if (-not (Test-WinttyOwnedStateBase $ownedTree.Path $ownedTree.Token -TempRoot $fakeTemp)) {
            $failed.Add('an owned tree with its own token is not recognised')
        }
        if (Test-WinttyOwnedStateBase $ownedTree.Path ('0' * 32) -TempRoot $fakeTemp) {
            $failed.Add('an owned tree is recognised with the wrong token')
        }
        if (Test-WinttyOwnedStateBase $ownedTree.Path '' -TempRoot $fakeTemp) {
            $failed.Add('an owned tree is recognised with no token at all')
        }
        $unmarked = Join-Path $fakeTemp 'unmarked'
        New-Item -ItemType Directory -Force -Path $unmarked | Out-Null
        if (Test-WinttyOwnedStateBase $unmarked $ownedTree.Token -TempRoot $fakeTemp) {
            $failed.Add('a tree with no marker is recognised as owned')
        }
        $viaJunction = New-WinttyOwnedStateBase -Under $junction
        if (Test-WinttyOwnedStateBase $viaJunction.Path $viaJunction.Token -TempRoot $fakeTemp) {
            $failed.Add('an owned tree reached through a junction out of temp is recognised')
        }

        # The same-exe gate resolves a relative path against the PowerShell
        # location, never the process directory, which is moved elsewhere
        # for the case so the two cannot agree by accident.
        $relRoot = Join-Path $jcase 'rel'
        New-Item -ItemType Directory -Force -Path $relRoot | Out-Null
        $relExe = Join-Path $relRoot 'Wintty.exe'
        $cwd = [Environment]::CurrentDirectory
        Push-Location -LiteralPath $relRoot
        [Environment]::CurrentDirectory = $temp
        try {
            $threw = $false
            try { Assert-NoWinttyFrom -ExePath 'Wintty.exe' -Instances @([pscustomobject]@{ Id = 9; Path = $relExe }) } catch { $threw = $true }
            if (-not $threw) { $failed.Add('Assert-NoWinttyFrom does not resolve a relative exe against the PowerShell location') }
            $threw = $false
            try { Assert-NoWinttyFrom -ExePath 'Wintty.exe' -Instances @($user) } catch { $threw = $true }
            if ($threw) { $failed.Add('Assert-NoWinttyFrom refuses an instance of another exe') }
        }
        finally {
            [Environment]::CurrentDirectory = $cwd
            Pop-Location
        }

        # The timing warning names other instances and never the run's own.
        $w = $null
        Write-WinttyTimingNeighbourWarning -ExePath $exe -Instances @($user) -WarningVariable w -WarningAction SilentlyContinue
        if (@($w).Count -ne 1 -or "$($w[0])" -notmatch '\b101\b') {
            $failed.Add('the timing warning does not name a Wintty from another exe')
        }
        $w = $null
        Write-WinttyTimingNeighbourWarning -ExePath $exe -Instances @([pscustomobject]@{ Id = 7; Path = $exe }) -WarningVariable w -WarningAction SilentlyContinue
        if (@($w).Count -ne 0) { $failed.Add('the timing warning fires over the run''s own exe') }

        # The preflight judges what staging cannot change, the AUMID included.
        $pre = @{ ExePath = $exe; Instances = @($user); Registrations = $userRegistrations }
        try {
            if ((Test-WinttyCoexistencePreflight @pre).Allowed) {
                $failed.Add('the preflight allows a build whose AUMID it cannot read')
            }
            $v = Test-WinttyCoexistencePreflight @pre -AumId 'com.example.mine'
            if (-not $v.Allowed) { $failed.Add("the preflight refuses a build handed its AUMID ($($v.Reasons -join ' | '))") }
            if ((Test-WinttyCoexistencePreflight @pre -AumId 'Vendor.Wintty.User').Allowed) {
                $failed.Add('the preflight allows a build of the same edition as a running instance')
            }
        }
        catch { $failed.Add("the preflight threw: $($_.Exception.Message)") }

        Case 'daemon data dir in a sibling of temp' $false @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_DATA_DIR = (Join-Path $tempSibling 'd') }) }
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
        finally {
            Remove-Item -LiteralPath $sweepRoot -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $vpkRoot -Recurse -Force -ErrorAction SilentlyContinue
            # The junction goes first, as a link: deleting it never touches
            # what it points at.
            if (Test-Path -LiteralPath $junction) { try { [System.IO.Directory]::Delete($junction) } catch { } }
            Remove-Item -LiteralPath $jcase -Recurse -Force -ErrorAction SilentlyContinue
        }

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
namespace Ghostty.Core { internal static class AppIdentity { public const string StateDirName = "WinttyFixture"; } }
'@

Assert-True ((Get-WinttyBuildAumid $mineImage) -ceq 'com.example.fixture') 'the AUMID is read out of the Ghostty.Core.dll beside the exe'
Assert-True ((Get-WinttyBuildStateDirName $mineImage) -ceq 'WinttyFixture') 'the state dir name is read out of the Ghostty.Core.dll beside the exe'
Assert-True ($null -eq (Get-WinttyBuildStateDirName $userImage)) 'no Ghostty.Core.dll, no state dir name'

$isolated = @{
    WINTTY_TEST_CONFIG = '1'
    XDG_CONFIG_HOME    = (Join-Path $root 'cfg')
    WINTTY_STATE_BASE  = (Join-Path $root 'state')
    XDG_STATE_HOME     = (Join-Path $root 'state\xdg-state')
    XDG_CACHE_HOME     = (Join-Path $root 'state\xdg-cache')
    WINTTY_SESSIOND_PIPE = '\\.\pipe\winttyd-test-' + [guid]::NewGuid().ToString('N')
}
$config = "windows-single-instance = false`nquick-terminal-key = $(Get-WinttyHarnessQuickTerminalKey)`n"
# Live process table, narrowed to this run's stand-ins: the cases must not
# depend on which real instances happen to be open on the machine (the live
# end-to-end run beside a real one is a separate check).
$standIns = { @(Get-WinttyInstances | Where-Object { $_.Path -and (ConvertTo-WinttyPathKey $_.Path).StartsWith((ConvertTo-WinttyPathKey $root) + '\') }) }
$userRegistration = @{ 'com.example.user' = $userImage }

# A just-started process is in the table before its image path can be
# read (the path comes off its module list, which the loader has not
# finished): wait for it, or a check reads "unreadable" instead of the
# case under test.
function Wait-StandInReadable([int]$Id) {
    $until = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $until) {
        if (@(Get-WinttyInstances | Where-Object { $_.Id -eq $Id -and $_.Path }).Count -gt 0) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "stand-in pid $Id never showed a readable image path"
}

$userProc = $null; $mineProc = $null
try {
    $userProc = Start-Process -FilePath $userImage -ArgumentList '-n', '120', '127.0.0.1' -WindowStyle Hidden -PassThru
    $userStarted = $userProc.StartTime
    Wait-StandInReadable $userProc.Id

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
    Wait-StandInReadable $mineProc.Id

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
       Find = 'if ($single.Count -eq 0 -or $notOff.Count -gt 0) {'; Replace = 'if ($false) {' },
    @{ Name = 'single-instance: only the last line read (the old rule)'
       Find = '$notOff = @($single | Where-Object'; Replace = '$notOff = @($single | Select-Object -Last 1 | Where-Object' },
    @{ Name = 'single-instance: only the first line read'
       Find = '$notOff = @($single | Where-Object'; Replace = '$notOff = @($single | Select-Object -First 1 | Where-Object' },
    @{ Name = 'single-instance may be left at its default'
       Find = 'if ($single.Count -eq 0 -or $notOff'; Replace = 'if ($false -or $notOff' },
    @{ Name = 'config keys matched case-sensitively'
       Find = '.Equals($Key, [StringComparison]::OrdinalIgnoreCase)'; Replace = '.Equals($Key, [StringComparison]::Ordinal)' },
    @{ Name = 'a bare CR does not end a config line'
       Find = '($ConfigText -split "\r\n|\r|\n")'; Replace = '($ConfigText -split "\r?\n")' },
    @{ Name = 'quick-terminal-key not checked'
       Find = 'if ($chords.Count -eq 0 -or @($chords | Where-Object { $_ -ine $chord }).Count -gt 0) {'; Replace = 'if ($false) {' },
    @{ Name = 'quick-terminal-key may be left at its default'
       Find = 'if ($chords.Count -eq 0 -or @($chords'; Replace = 'if ($false -or @($chords' },
    @{ Name = 'quick-terminal-key: only the last line read'
       Find = '@($chords | Where-Object { $_ -ine $chord })'; Replace = '@($chords | Select-Object -Last 1 | Where-Object { $_ -ine $chord })' },
    @{ Name = 'the harness stages no chord'
       Find = 'return $text + "quick-terminal-key = $(Get-WinttyHarnessQuickTerminalKey)`n"'; Replace = 'return $text' },
    @{ Name = 'the harness chord overrides the config''s own'
       Find = "if ((Get-WinttyConfigValues `$ConfigText 'quick-terminal-key').Count -gt 0) { return `$ConfigText }"; Replace = '' },
    @{ Name = 'a Velopack install may be launched'
       Find = 'if ($installDir -and [System.IO.File]::Exists('; Replace = 'if ($false -and [System.IO.File]::Exists(' },
    @{ Name = 'install bases not checked'
       Find = 'foreach ($base in @($InstallBases)) {'; Replace = 'foreach ($base in @()) {' },
    @{ Name = 'Program Files is not a default install base'
       Find = "[Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86')"; Replace = "'Z:\nowhere'" },
    @{ Name = 'the under-temp check drops the separator'
       Find = '$p.StartsWith($r + ''\'', [StringComparison]::Ordinal)'; Replace = '$p.StartsWith($r, [StringComparison]::Ordinal)' },
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
    @{ Name = 'the capture gate does not hit-test'
       Find = '        if ($root -ne $Hwnd64 -and'; Replace = '        if ($false -and' },
    @{ Name = 'a passed AUMID overrides the build''s own'
       Find = '            if ($AumId -and $AumId -ine $ownAumId) {'; Replace = '            if ($false) {' },
    @{ Name = 'an unreadable path segment is skipped'
       Find = '            else { return $false }'; Replace = '            else { $attrs = $null }' },
    @{ Name = 'the session-state helper does not refuse'
       Find = '    if ($others.Count -eq 0) { return }' + "`n" + '    throw ("HARNESS: skipped:'; Replace = '    return' + "`n" + '    throw ("HARNESS: skipped:' },
    @{ Name = 'the session-state restore is not in a finally'
       Find = '    try { $null = & $Change }' + "`n" + '    finally { if ($Restore) { $null = & $Restore } }'
       Replace = '    $null = & $Change' + "`n" + '    if ($Restore) { $null = & $Restore }' },
    @{ Name = 'native state dirs not checked'
       Find = "foreach (`$name in 'XDG_STATE_HOME', 'XDG_CACHE_HOME') {"; Replace = 'foreach ($name in @()) {' },
    @{ Name = 'a junction under temp counts as under temp'
       Find = 'if ($null -ne $attrs -and ($attrs -band [System.IO.FileAttributes]::ReparsePoint)) { return $false }'; Replace = '' },
    @{ Name = 'a relative exe resolves against the process directory'
       Find = '$ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)'; Replace = '' },
    @{ Name = 'any marker proves ownership'
       Find = 'return $held.Length -gt 0 -and $held -ceq $Token.Trim()'; Replace = 'return $true' },
    @{ Name = 'the preflight drops the AUMID'
       Find = "    if (`$AumId) { `$check.AumId = `$AumId }`n    if (`$PSBoundParameters.ContainsKey('Instances'))"
       Replace = "    if (`$PSBoundParameters.ContainsKey('Instances'))" },
    @{ Name = 'the timing warning counts the run''s own instance'
       Find = '-not ($_.Path -and $mine -contains (ConvertTo-WinttyPathKey $_.Path)) })'; Replace = '$true })' },
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

Remove-Item -LiteralPath $script:aumFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
if ($script:fails -gt 0) { Write-Host "$script:fails failure(s)" -ForegroundColor Red; exit 1 }
Write-Host 'all green'
exit 0
