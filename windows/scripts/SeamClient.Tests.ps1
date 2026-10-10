#requires -Version 7
# Tests for lib/seam-client.ps1's session-daemon mint: the private pipe a
# seam launch names for builds that carry a daemon, and the daemon's data,
# log and bin dirs under the session's own temp root.
#
# Why the mint is the harness's job: in test mode (WINTTY_TEST_CONFIG, which
# Start-SeamSession arms) the app refuses to resolve a daemon pipe the
# environment does not name, and a daemon it auto-spawns refuses to start
# with a state path under the real per-edition base. Without a mint every
# sessioned seam leg degrades to a nothing-attached pane; with one, the app
# and its daemon live on a pipe nothing else answers, and their state dies
# with the run's temp root.
#
# Same shape as WinttyProcess.Tests.ps1: plain asserts, no Pester, run it
# directly (exit 0 green, 1 red):
#
#     pwsh -NoProfile -File windows/scripts/SeamClient.Tests.ps1
#
# Layers:
#   1. the minter's own properties: the private test spelling, fresh every
#      call, and never a per-user (SID-carrying) name;
#   2. the coexistence guard's verdict on a minted launch environment,
#      both ways, against injected instances;
#   3. the wiring of Start-SeamSession / Stop-SeamSession: the mint happens
#      before the guard and before anything launches, the daemon's dirs sit
#      under the session's temp root, and every variable is restored;
#   4. mutation rows: the layers again, against a copy of the library with
#      one rule broken. Every row must turn something red;
#   5. the private state tree every session gets by default, the opt-out,
#      and the crash oracle over that tree, driven through Start-SeamSession
#      up to a stand-in for its guard;
#   6. a scan of the harnesses: each gates on the exe under test rather than
#      on any running Wintty, and none reads the per-user crash.log;
#   7. the teardown hook a caller hands in: once per session on every path
#      that ends in a teardown, after the app is gone and while the session's
#      temp root is still on disk, with the tree deleted whether the hook
#      threw or not;
#   8. the birth harness's half of that contract: it takes the caller's hook
#      as a [scriptblock], forwards it to every session it starts, hands it to
#      no Stop (Stop appends, so the hook would run twice), and records a
#      teardown failure instead of throwing out of its finally.
#
# Nothing here launches Wintty: the guard cases inject stand-in instances,
# the wiring cases read the library's text, the state-base cases stop
# at a stand-in guard that refuses before anything starts, and the teardown
# hook cases answer the launch, the splash wait and the pipe from stand-ins.
param(
    # The library under test. Overridable so the mutation rows (and a red
    # proof against an earlier copy) can point at a file that is not the
    # sibling on disk.
    [string]$SeamClientPath = (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
)

$ErrorActionPreference = 'Stop'

$script:fails = 0
function Assert-True($cond, $msg) {
    if (-not $cond) { $script:fails++; Write-Host "FAIL: $msg" -ForegroundColor Red }
    else { Write-Host "ok: $msg" -ForegroundColor Green }
}

$script:processLib = Join-Path $PSScriptRoot 'lib/wintty-process.ps1'

# ---- layer 1: the minter ------------------------------------------------------

# Runs the minter cases against the library at $LibPath and returns the
# names of the cases that went wrong. Dot-sourced into a child scope, so a
# mutant copy never leaks its functions into the rows that follow.
function Invoke-MinterCases([string]$LibPath) {
    return & {
        param($processLib, $lib)
        . $processLib
        # A copy that cannot even be dot-sourced (an Add-Type recompile
        # under Stop, say) is a red of its own, not a reason to die before
        # the rows that follow.
        try { . $lib } catch {
            $failed = [System.Collections.Generic.List[string]]::new()
            $failed.Add("the library cannot be dot-sourced here: $($_.Exception.Message)")
            return , $failed
        }
        $failed = [System.Collections.Generic.List[string]]::new()
        if (-not (Get-Command New-SeamSessionPipeName -ErrorAction SilentlyContinue)) {
            $failed.Add('New-SeamSessionPipeName does not exist: nothing mints the session daemon pipe')
            return , $failed
        }
        $pipe = New-SeamSessionPipeName
        if ($pipe -notmatch '^\\\\\.\\pipe\\winttyd-test-[0-9a-f]{32}$') {
            $failed.Add("minted pipe '$pipe' is not the private test shape \\.\pipe\winttyd-test-<128 bits of hex>")
        }
        # A name carrying a user SID is a per-user name: the real daemon's
        # own spelling, which is what the guard's pipe rule refuses.
        if ($pipe -match '(?i)-S-1-\d+(-\d+)+') {
            $failed.Add("minted pipe '$pipe' carries a user SID, so it names the real per-user daemon")
        }
        if ((New-SeamSessionPipeName) -ceq $pipe) {
            $failed.Add('the minted pipe is not fresh: two calls returned one name')
        }
        return , $failed
    } $script:processLib $LibPath
}

# ---- layer 2: the guard's verdict on a minted environment ---------------------

function Invoke-GuardCases([string]$LibPath) {
    return & {
        param($processLib, $lib)
        . $processLib
        try { . $lib } catch {
            $failed = [System.Collections.Generic.List[string]]::new()
            $failed.Add("the library cannot be dot-sourced here: $($_.Exception.Message)")
            return , $failed
        }
        $failed = [System.Collections.Generic.List[string]]::new()
        if (-not (Get-Command New-SeamSessionPipeName -ErrorAction SilentlyContinue)) {
            $failed.Add('New-SeamSessionPipeName does not exist: no minted environment to judge')
            return , $failed
        }
        $temp = [System.IO.Path]::GetTempPath()
        $exe = 'C:\builds\mine\out\Wintty.exe'
        $userPath = 'C:\Users\someone\AppData\Local\Vendor\Wintty.User\current\Wintty.exe'
        $user = [pscustomobject]@{ Id = 101; Path = $userPath }
        $chord = Get-WinttyHarnessQuickTerminalKey
        $config = "window-save-state = never`nwindows-single-instance = false`nquick-terminal-key = $chord`n"
        $sessiondRoot = Join-Path $temp ("wintty-seamsd-case-" + [guid]::NewGuid().ToString('N'))
        $minted = @{
            WINTTY_TEST_CONFIG    = '1'
            XDG_CONFIG_HOME       = (Join-Path $temp 'wintty-seam-case')
            WINTTY_STATE_BASE     = (Join-Path $temp 'wintty-seam-case\state')
            XDG_STATE_HOME        = (Join-Path $temp 'wintty-seam-case\state\xdg-state')
            XDG_CACHE_HOME        = (Join-Path $temp 'wintty-seam-case\state\xdg-cache')
            WINTTY_SESSIOND_PIPE  = New-SeamSessionPipeName
            WINTTY_SESSIOND_DATA_DIR  = (Join-Path $sessiondRoot 'data')
            WINTTY_SESSIOND_LOG_FILE  = (Join-Path $sessiondRoot 'logs\sessiond.log')
            WINTTY_SESSIOND_BIN_DIR   = (Join-Path $sessiondRoot 'bin')
        }

        function EnvWith([hashtable]$Changes) {
            $e = $minted.Clone()
            foreach ($k in $Changes.Keys) {
                if ($null -eq $Changes[$k]) { $e.Remove($k) } else { $e[$k] = $Changes[$k] }
            }
            return $e
        }

        function Case([string]$Name, [bool]$WantAllowed, [hashtable]$Over = @{}) {
            $a = @{
                ExePath       = $exe
                ConfigText    = $config
                AumId         = 'com.example.mine'
                Environment   = $minted.Clone()
                Instances     = @($user)
                Registrations = @{ 'Vendor.Wintty.User' = $userPath }
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

        # The whole point: a seam launch may run beside the user's Wintty
        # with its daemon pipe named and the daemon's state under temp.
        Case 'minted daemon environment, beside another edition' $true
        # The failure the mint exists to prevent: a per-user name reaches
        # the user's real daemon.
        Case 'a per-user daemon pipe is refused' $false @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_PIPE = '\\.\pipe\winttyd-Vendor.Wintty.User-S-1-5-21-11-22-33-1001' }) }
        # And the daemon's own files may not leave temp either.
        Case 'daemon data dir outside temp is refused' $false @{
            Environment = (EnvWith @{ WINTTY_SESSIOND_DATA_DIR = 'C:\Users\someone\AppData\Local\Vendor\sessiond' }) }
        return , $failed
    } $script:processLib $LibPath
}

# ---- layer 3: the wiring of Start/Stop-SeamSession -----------------------------

# Pure text: launching the app is the acceptance harnesses' job, and the
# properties worth pinning here (what is set, where it sits, what is
# restored, and in which order) are all visible in the file.
function Invoke-WiringCases([string]$LibPath) {
    $failed = [System.Collections.Generic.List[string]]::new()
    $lines = [System.IO.File]::ReadAllLines($LibPath)

    # The first line matching a pattern, or -1.
    function FirstIndex([string]$Pattern) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $Pattern) { return $i }
        }
        return -1
    }

    # The minter exists and mints: the private test prefix, built from the
    # token, not a fixed or per-user name.
    $minter = FirstIndex 'function New-SeamSessionPipeName'
    if ($minter -lt 0) {
        $failed.Add('New-SeamSessionPipeName is not defined')
    }
    else {
        $body = ($lines[$minter..([Math]::Min($minter + 7, $lines.Count - 1))] -join "`n")
        if ($body -notmatch 'winttyd-test-') {
            $failed.Add('the minter does not build the private winttyd-test- name')
        }
        if ($body -notmatch 'New-SeamToken') {
            $failed.Add('the minter does not take its randomness from New-SeamToken')
        }
    }

    # The session table names the pipe it will set.
    if ((FirstIndex 'SessionPipe\s*=\s*New-SeamSessionPipeName') -lt 0) {
        $failed.Add('the session table does not mint the daemon pipe (SessionPipe = New-SeamSessionPipeName)')
    }

    # Every sessiond variable is set, from the session's own root, never a
    # caller's path or a literal outside it.
    $sets = @{
        'WINTTY_SESSIOND_PIPE'      = '\$env:WINTTY_SESSIOND_PIPE\s*=\s*\$session\.SessionPipe'
        'WINTTY_SESSIOND_DATA_DIR'  = '\$env:WINTTY_SESSIOND_DATA_DIR\s*=\s*Join-Path \$session\.SessiondRoot'
        'WINTTY_SESSIOND_LOG_FILE'  = '\$env:WINTTY_SESSIOND_LOG_FILE\s*=\s*Join-Path \$session\.SessiondRoot'
        'WINTTY_SESSIOND_BIN_DIR'   = '\$env:WINTTY_SESSIOND_BIN_DIR\s*=\s*Join-Path \$session\.SessiondRoot'
    }
    foreach ($name in $sets.Keys) {
        if ((FirstIndex $sets[$name]) -lt 0) {
            $failed.Add("Start-SeamSession does not set $name from the session's sessiond root")
        }
    }

    # The order that makes the mint load-bearing: the environment is named
    # before the coexistence guard reads it, and the guard runs before
    # anything launches.
    $pipeSet = FirstIndex '\$env:WINTTY_SESSIOND_PIPE\s*='
    $guard = FirstIndex 'Assert-WinttyCoexistence'
    $launch = FirstIndex 'Start-Process @startArgs'
    if ($pipeSet -lt 0 -or $guard -lt 0 -or $launch -lt 0 -or
        -not ($pipeSet -lt $guard -and $guard -lt $launch)) {
        $failed.Add('the daemon pipe is not minted before the coexistence guard, ahead of the launch')
    }

    # And the session's temp root is where the daemon's tree lives.
    if ((FirstIndex '\$session\.SessiondRoot\s*=\s*Join-Path \$tempXdg') -lt 0) {
        $failed.Add('the sessiond root is not under the session temp root ($tempXdg)')
    }

    # Save and restore, paired by member name: each variable's original is
    # captured in the session table and put back in Stop-SeamSession, both
    # branches of it (a value to restore, and the removal when there was
    # none).
    $pairs = @{
        'WINTTY_SESSIOND_PIPE'     = 'OrigSessionPipe'
        'WINTTY_SESSIOND_DATA_DIR' = 'OrigSessiondDataDir'
        'WINTTY_SESSIOND_LOG_FILE' = 'OrigSessiondLogFile'
        'WINTTY_SESSIOND_BIN_DIR'  = 'OrigSessiondBinDir'
    }
    foreach ($name in $pairs.Keys) {
        $orig = $pairs[$name]
        if ((FirstIndex ("$orig\s*=\s*if \(Test-Path Env:$name\)")) -lt 0) {
            $failed.Add("$name's original is not captured ($orig)")
        }
        if ((FirstIndex ("\`$env:$name\s*=\s*\`$Session\.$orig")) -lt 0) {
            $failed.Add("$name is not restored from $orig in Stop-SeamSession")
        }
        if ((FirstIndex ("Remove-Item Env:$name ")) -lt 0) {
            $failed.Add("$name is not removed when the caller had none")
        }
    }
    return , $failed
}

# ---- layer 5: the private state tree and the crash oracle ---------------------

# Start-SeamSession for real, up to its coexistence guard, which is replaced
# by a stand-in that records the environment the launch would inherit (and,
# for one case, writes the crash.log a dying app would) and then refuses.
# A refusal launches nothing: Start-SeamSession tears the session down and
# rethrows. The exe is an empty file in temp, so the teardown's sweep
# matches no real process.
function Invoke-StateBaseCases([string]$LibPath) {
    return & {
        param($processLib, $lib)
        . $processLib
        try { . $lib } catch {
            $failed = [System.Collections.Generic.List[string]]::new()
            $failed.Add("the library cannot be dot-sourced here: $($_.Exception.Message)")
            return , $failed
        }
        $failed = [System.Collections.Generic.List[string]]::new()
        foreach ($name in 'Get-SeamSessionMark', 'Test-SeamCrashLogWritten', 'Get-SeamRunCrashLogs', 'New-WinttyOwnedStateBase') {
            if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
                $failed.Add("$name does not exist: there is no crash oracle over the session's own state tree")
                return , $failed
            }
        }
        $temp = [System.IO.Path]::GetTempPath()
        $root = Join-Path $temp ("wintty-seam-statecase-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        $exe = Join-Path $root 'Wintty.exe'
        [System.IO.File]::WriteAllBytes($exe, [byte[]]@())
        $cap = @{}
        function Assert-WinttyCoexistence {
            param($ExePath, $ConfigText, $AumId, $Context)
            $cap.StateBase = $env:WINTTY_STATE_BASE
            $cap.XdgState = $env:XDG_STATE_HOME
            $cap.XdgCache = $env:XDG_CACHE_HOME
            $cap.Token = $env:WINTTY_STATE_BASE_TOKEN
            $cap.AumId = $AumId
            $cap.TempXdg = $env:XDG_CONFIG_HOME
            if ($cap.Crash -and $env:WINTTY_STATE_BASE) {
                $dir = Join-Path $env:WINTTY_STATE_BASE 'Wintty'
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                [System.IO.File]::WriteAllText((Join-Path $dir 'crash.log'), 'state-case crash')
            }
            throw 'state-case refusal'
        }
        function Start-Refused([hashtable]$Extra = @{}) {
            $cap.Clear()
            $cap.Crash = [bool]$Extra['Crash']
            $Extra.Remove('Crash')
            try { Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' @Extra | Out-Null }
            catch { if ("$_" -notmatch 'state-case refusal') { $failed.Add("Start-SeamSession threw something else: $_") } }
        }
        $saved = @{}
        foreach ($n in 'WINTTY_STATE_BASE', 'WINTTY_STATE_BASE_TOKEN', 'XDG_STATE_HOME', 'XDG_CACHE_HOME') {
            $saved[$n] = [System.Environment]::GetEnvironmentVariable($n)
        }
        try {
            foreach ($n in @($saved.Keys)) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }

            # The default: a fresh tree inside the session's own temp root,
            # with libghostty's native state inside that tree too.
            Start-Refused
            if (-not $cap.StateBase) {
                $failed.Add('by default the launch inherits no WINTTY_STATE_BASE, so it shares the per-user state tree')
            }
            elseif (-not (Test-WinttyPathUnder $cap.StateBase $cap.TempXdg)) {
                $failed.Add("by default WINTTY_STATE_BASE '$($cap.StateBase)' is not inside the session's temp root")
            }
            foreach ($n in 'XdgState', 'XdgCache') {
                if (-not $cap[$n] -or -not $cap.StateBase -or -not (Test-WinttyPathUnder $cap[$n] $cap.StateBase)) {
                    $failed.Add("by default the native $n dir '$($cap[$n])' is not inside the session's state tree")
                }
            }
            foreach ($n in $saved.Keys) {
                if (Test-Path "Env:$n") { $failed.Add("Stop-SeamSession left $n set although the caller had none") }
            }

            # The opt-out is the real per-user tree, whatever the caller had.
            $env:WINTTY_STATE_BASE = Join-Path $root 'callers-anything'
            $env:XDG_STATE_HOME = Join-Path $root 'callers-xdg-state'
            Start-Refused @{ SharedStateBase = $true }
            if ($cap.StateBase -or $cap.XdgState -or $cap.XdgCache) {
                $failed.Add("-SharedStateBase did not hand the launch the per-user tree (state '$($cap.StateBase)', native '$($cap.XdgState)')")
            }
            if ($env:WINTTY_STATE_BASE -ne (Join-Path $root 'callers-anything') -or $env:XDG_STATE_HOME -ne (Join-Path $root 'callers-xdg-state')) {
                $failed.Add('Stop-SeamSession did not restore the caller''s variables after -SharedStateBase')
            }
            Remove-Item Env:WINTTY_STATE_BASE, Env:XDG_STATE_HOME -ErrorAction SilentlyContinue

            # A tree under temp the run does not own is NOT adopted: a pane
            # of a harness-launched Wintty inherits its app's tree, and a
            # harness started there would otherwise share it.
            $stray = Join-Path $root 'stray-state'
            New-Item -ItemType Directory -Force -Path $stray | Out-Null
            $env:WINTTY_STATE_BASE = $stray
            Start-Refused
            if ($cap.StateBase -eq $stray -or -not $cap.StateBase) {
                $failed.Add("an inherited WINTTY_STATE_BASE this run does not own was adopted (saw '$($cap.StateBase)')")
            }

            # An owned tree is adopted with its token, and the token is kept
            # out of the app's environment; -PrivateStateBase mints over it,
            # as it did when it was the opt-in.
            $ownedTree = New-WinttyOwnedStateBase -Under $root
            $env:WINTTY_STATE_BASE = $ownedTree.Path
            $env:WINTTY_STATE_BASE_TOKEN = $ownedTree.Token
            Start-Refused
            if ($cap.StateBase -ne $ownedTree.Path) { $failed.Add("an owned tree with its token was not adopted (saw '$($cap.StateBase)')") }
            if ($cap.Token) { $failed.Add('the ownership token reached the launch environment') }
            $env:WINTTY_STATE_BASE_TOKEN = ('0' * 32)
            Start-Refused
            if ($cap.StateBase -eq $ownedTree.Path) { $failed.Add('an owned tree was adopted with the wrong token') }
            $env:WINTTY_STATE_BASE_TOKEN = $ownedTree.Token
            Start-Refused @{ PrivateStateBase = $true }
            if ($cap.StateBase -eq $ownedTree.Path -or -not $cap.StateBase) {
                $failed.Add("-PrivateStateBase did not mint a fresh tree over the caller's (saw '$($cap.StateBase)')")
            }
            if ($env:WINTTY_STATE_BASE -ne $ownedTree.Path -or $env:WINTTY_STATE_BASE_TOKEN -ne $ownedTree.Token) {
                $failed.Add("Stop-SeamSession did not restore the caller's WINTTY_STATE_BASE and token")
            }
            Remove-Item Env:WINTTY_STATE_BASE, Env:WINTTY_STATE_BASE_TOKEN -ErrorAction SilentlyContinue

            # -AumId reaches the guard (a NativeAOT publish cannot be read).
            Start-Refused @{ AumId = 'com.example.aot' }
            if ($cap.AumId -ne 'com.example.aot') { $failed.Add("-AumId did not reach the guard (saw '$($cap.AumId)')") }

            # The crash oracle: a crash written into the private tree during
            # a start that never handed a session back still counts, after
            # the tree itself is gone, and only for the sessions after the mark.
            $mark = Get-SeamSessionMark
            Start-Refused @{ Crash = $true }
            if (Test-Path -LiteralPath $cap.TempXdg) { $failed.Add('the refused session left its temp root behind') }
            if (-not (Test-SeamCrashLogWritten -Since $mark)) {
                $failed.Add('a crash.log written in the private tree during a failed start is not reported after teardown')
            }
            elseif (@(Get-SeamRunCrashLogs -Since $mark)[0].Text -ne 'state-case crash') {
                $failed.Add('the reported crash.log does not carry the text the app wrote')
            }
            $after = Get-SeamSessionMark
            Start-Refused
            if (Test-SeamCrashLogWritten -Since $after) {
                $failed.Add('a session that wrote no crash.log is reported as crashing (the mark does not scope the oracle)')
            }

            # A shared session reads the per-user crash.log under the
            # edition's own dir name; one it cannot read reports instead of
            # going quiet.
            $sharedLogs = @(Get-SeamCrashLogs @{ StateBase = $null; ExePath = $exe; Stamp = (Get-Date) })
            if ($sharedLogs.Count -ne 1 -or $sharedLogs[0].Text -notmatch 'cannot be read') {
                $failed.Add('a shared session whose state dir name cannot be read reports a clean crash oracle')
            }
        }
        finally {
            foreach ($n in @($saved.Keys)) { if ($null -ne $saved[$n]) { Set-Item "Env:$n" $saved[$n] } else { Remove-Item "Env:$n" -ErrorAction SilentlyContinue } }
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
        return , $failed
    } $script:processLib $LibPath
}

# ---- layer 6: the harness gates, scanned ---------------------------------------

# Every harness here gates on the exe under test, never on any running
# Wintty: the blanket check is left for a harness with a reason no isolation
# removes, which has to say it. Every launch of the exe is preceded by the
# coexistence guard in the same function. No harness reads the per-user
# crash.log, which every other Wintty on the machine writes too. And a
# harness that changes session-wide state (system parameters, the HKCU
# theme and desktop keys, the wallpaper, the clipboard, other apps' windows)
# does it through the session-state helpers, which refuse beside another
# Wintty.
function Invoke-GateScanCases(
    [string]$ScriptDir = $PSScriptRoot,
    [string]$JustfilePath = (Join-Path $PSScriptRoot '..\..\justfile')
) {
    $failed = [System.Collections.Generic.List[string]]::new()
    $A = [System.Management.Automation.Language.Ast]
    # The sanctioned blanket refusals, each with a reason no isolation
    # removes: WER LocalDumps is keyed on the image name, so it covers every
    # running Wintty; the splash race measures the single-instance election
    # itself; the cdb capture launches the app under a debugger with
    # single-instance on and the per-user tree.
    $blanketAllowed = @('seam-crash-dump.ps1', 'splash-single-instance-race.ps1', 'seam-cdb.ps1')
    # The developer's own launcher (just run-win) is not a harness: it opens
    # the app the way a user does, on purpose.
    $notHarness = @('run-win-launch.ps1')
    # Scripts that launch the exe themselves without the guard or a blanket
    # refusal. None is run by a just recipe, the fuzz suite or the AOT fuzz,
    # and none ever had a gate this conversion loosened; each is listed so a
    # new one cannot join them silently, and listing is refused once a
    # recipe or a suite runs it.
    $unguardedAllowed = @{
        'mouse-smoke-run.ps1'  = 'the operator drives it by hand and quits the app themselves'
        'config-save-race.ps1' = 'a standalone repro, run by hand'
        'crash-canary.ps1'     = 'a standalone crash repro against a NativeAOT publish, run by hand'
        'crash-matrix.ps1'     = 'a standalone crash repro against a NativeAOT publish, run by hand'
        'seam-bisect.ps1'      = 'a bisect driver, run by hand'
        'seam-probe.ps1'       = 'a one-off probe, run by hand'
    }

    # What a recipe or an orchestrator runs: every script the justfile
    # names, the fuzz suite's manifest, and the AOT fuzz's harness list.
    $reachable = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if (Test-Path -LiteralPath $JustfilePath) {
        foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($JustfilePath), 'windows/scripts/([\w.-]+\.ps1)')) {
            [void]$reachable.Add($m.Groups[1].Value)
        }
    }
    $suite = Join-Path $ScriptDir 'fuzz-suite.ps1'
    if (Test-Path -LiteralPath $suite) {
        foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($suite), "script = '([\w.-]+\.ps1)'")) {
            [void]$reachable.Add($m.Groups[1].Value)
        }
    }
    $aot = Join-Path $ScriptDir 'aot-fuzz.ps1'
    if (Test-Path -LiteralPath $aot) {
        foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($aot), "'([\w.-]+\.ps1)'")) {
            [void]$reachable.Add($m.Groups[1].Value)
        }
    }

    # An exe variable is how every launcher here names the app; an
    # exe-carrying variable is one whose assignment mentions an exe variable
    # (a debugger's argument line, a ProcessStartInfo). A value handed to a
    # parameter named after an exe (-ExePath $x for a child harness) is not
    # a launch of it.
    $exeVar = '(?i)^\(?\$(\w+[:.])?\w*exe\w*'
    $sessionSecondary = '(?i)^\$\w+\.ExePath$'
    $writers = @('Set-SpiUint', 'Set-SpiHoverTime', 'Set-HighContrastFlags', 'Set-DesktopWallpaper',
        'Set-DesktopPolarity', 'Set-Clipboard', 'scb', 'Clear-Desktop')
    $regWriters = @('Set-ItemProperty', 'New-ItemProperty', 'Remove-ItemProperty', 'New-Item', 'Remove-Item')
    $libKeys = '(?i)\$script:(Personalize|Themes|Desktop|Dwm|Accent|BackdropDesktop)Key'

    function EnclosingFunction($node) {
        $n = $node.Parent
        while ($null -ne $n -and $n -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $n = $n.Parent }
        return $n
    }
    function InsideHelperBlock($node) {
        $n = $node.Parent
        while ($null -ne $n) {
            if ($n -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -and
                $n.Parent -is [System.Management.Automation.Language.CommandParameterAst] -and
                $n.Parent.Parent -is [System.Management.Automation.Language.CommandAst] -and
                $n.Parent.Parent.GetCommandName() -eq 'Invoke-WinttySessionStateChange') { return $true }
            if ($n -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -and
                $n.Parent -is [System.Management.Automation.Language.CommandAst] -and
                $n.Parent.GetCommandName() -eq 'Invoke-WinttySessionStateChange') { return $true }
            $n = $n.Parent
        }
        return $false
    }
    # Whether some call named $Name precedes $node within the same function
    # (or the same top level).
    function PrecededBy($node, [string]$Name, $calls) {
        $fn = EnclosingFunction $node
        foreach ($c in $calls) {
            if ($c.GetCommandName() -ne $Name) { continue }
            if ([object]::ReferenceEquals((EnclosingFunction $c), $fn) -and $c.Extent.StartOffset -lt $node.Extent.StartOffset) { return $true }
        }
        return $false
    }

    foreach ($file in Get-ChildItem -LiteralPath $ScriptDir -Filter *.ps1 -File) {
        if ($file.Name -like '*.Tests.ps1') { continue }
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        $commands = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
        $reasoned = $false
        foreach ($call in @($commands | Where-Object { $_.GetCommandName() -eq 'Assert-NoWintty' })) {
            if ($blanketAllowed -notcontains $file.Name) {
                $failed.Add("$($file.Name):$($call.Extent.StartLineNumber) refuses any running Wintty; gate on the exe under test (Assert-NoWinttyFrom)")
            }
            elseif ($call.Extent.Text -notmatch '-Reason\b') {
                $failed.Add("$($file.Name):$($call.Extent.StartLineNumber) refuses any running Wintty without saying why (-Reason)")
            }
            elseif ($null -eq (EnclosingFunction $call)) { $reasoned = $true }
        }
        $usesSession = @($commands | Where-Object { $_.GetCommandName() -eq 'Start-SeamSession' }).Count -gt 0
        $carrying = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $hkcuVars = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($as in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
            if ($as.Left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { continue }
            $name = $as.Left.VariablePath.UserPath
            # An exe variable in the value, other than as the value of an
            # exe-named parameter (a session started with -ExePath $x carries
            # a session, which its guard already judged).
            foreach ($v in $as.Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                if ($v.Extent.Text -notmatch $exeVar) { continue }
                $parent = $v.Parent
                if ($parent -is [System.Management.Automation.Language.CommandAst]) {
                    $idx = [array]::IndexOf(@($parent.CommandElements), $v)
                    if ($idx -gt 0 -and $parent.CommandElements[$idx - 1] -is [System.Management.Automation.Language.CommandParameterAst] -and
                        $parent.CommandElements[$idx - 1].ParameterName -match '(?i)exe') { continue }
                }
                [void]$carrying.Add($name); break
            }
            if ($as.Right.Extent.Text -match '(?i)HKCU:|HKEY_CURRENT_USER') { [void]$hkcuVars.Add($name) }
        }
        function IsExeArg($el) {
            $t = $el.Extent.Text
            if ($t -match $exeVar) { return $true }
            if ($el -is [System.Management.Automation.Language.VariableExpressionAst] -and $carrying.Contains($el.VariablePath.UserPath)) { return $true }
            return $false
        }

        # ---- launches, judged one site at a time ----
        $launches = [System.Collections.Generic.List[object]]::new()
        foreach ($c in $commands) {
            $name = $c.GetCommandName()
            $els = $c.CommandElements
            $amp = $c.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Ampersand
            if ($name -ne 'Start-Process' -and -not $amp) { continue }
            $hit = $null
            $start = if ($amp) { 0 } else { 1 }
            for ($i = $start; $i -lt $els.Count; $i++) {
                $el = $els[$i]
                if ($el -is [System.Management.Automation.Language.CommandParameterAst]) {
                    # The value of an exe-named parameter is a child's input.
                    if ($el.ParameterName -match '(?i)exe' -and -not ($name -eq 'Start-Process' -and $el.ParameterName -like 'FilePath*')) { $i++ }
                    continue
                }
                if ($amp -and $i -eq 0 -and $el.Extent.Text -match '(?i)^(pwsh|powershell)') { break }
                if (IsExeArg $el) { $hit = $el; break }
            }
            if ($hit) { $launches.Add([pscustomobject]@{ Node = $c; Target = $hit.Extent.Text }) }
        }
        foreach ($m in $ast.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $n.Member.Extent.Text -eq 'new' -and $n.Expression.Extent.Text -match 'ProcessStartInfo\]$' }, $true)) {
            if ($m.Arguments.Count -gt 0 -and (IsExeArg $m.Arguments[0])) {
                $launches.Add([pscustomobject]@{ Node = $m; Target = $m.Arguments[0].Extent.Text })
            }
        }
        foreach ($l in $launches) {
            if ($reasoned -or $notHarness -contains $file.Name) { continue }
            if ($usesSession -and $l.Target -match $sessionSecondary) { continue }
            if (PrecededBy $l.Node 'Assert-WinttyCoexistence' $commands) { continue }
            if ($unguardedAllowed.ContainsKey($file.Name)) {
                if ($reachable.Contains($file.Name)) {
                    $failed.Add("$($file.Name) launches the exe without the coexistence guard and is run by a recipe or a suite now; it can no longer be listed as hand-run")
                }
                continue
            }
            $failed.Add("$($file.Name):$($l.Node.Extent.StartLineNumber) launches the exe ($($l.Target)) without the coexistence guard before it in the same function, and without a reasoned Assert-NoWintty")
        }

        # ---- session-wide state, judged one write at a time ----
        $cuVars = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($as in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
            if ($as.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $as.Right.Extent.Text -match '(?i)CurrentUser|HKEY_CURRENT_USER') { [void]$cuVars.Add($as.Left.VariablePath.UserPath) }
        }
        $writes = [System.Collections.Generic.List[object]]::new()
        foreach ($c in $commands) {
            $name = $c.GetCommandName()
            $text = $c.Extent.Text
            if ($writers -contains $name) { $writes.Add($c); continue }
            if ($name -eq 'Set-BackdropScene' -and $text -match '(?i)-Wallpaper\b') { $writes.Add($c); continue }
            # clip.exe writes the clipboard; reg.exe add/delete/import/copy/
            # restore under HKCU writes the user's registry.
            if ($name -match '(?i)^clip(\.exe)?$') { $writes.Add($c); continue }
            if ($name -match '(?i)^reg(\.exe)?$' -and $text -match '(?i)\s(add|delete|import|copy|restore|load)\s' -and
                $text -match '(?i)HKCU|HKEY_CURRENT_USER') { $writes.Add($c); continue }
            if ($regWriters -contains $name) {
                $hk = $text -match '(?i)HKCU:|HKEY_CURRENT_USER' -or $text -match $libKeys
                if (-not $hk) {
                    foreach ($v in $c.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                        if ($hkcuVars.Contains($v.VariablePath.UserPath)) { $hk = $true; break }
                    }
                }
                if ($hk) { $writes.Add($c) }
            }
        }
        foreach ($m in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)) {
            $member = $m.Member.Extent.Text
            $args0 = if ($m.Arguments.Count -gt 0) { $m.Arguments[0].Extent.Text } else { '' }
            # Any wrapper named after SystemParametersInfo, with any action
            # that does not name itself a GET: a numeric 0x1043 is a set.
            if ($member -match '(?i)SystemParametersInfo' -and $args0 -notmatch '(?i)GET') { $writes.Add($m) }
            elseif ($member -eq 'ShowWindow' -and $m.Arguments.Count -gt 1 -and $m.Arguments[1].Extent.Text -match '(?i)MINIMIZE') { $writes.Add($m) }
            elseif ($member -match '^Set(Text|Data|Image|Clipboard\w*)$' -and $m.Expression.Extent.Text -match '(?i)Clipboard\]$') { $writes.Add($m) }
            elseif ($member -match '^(SetValue|CreateSubKey|DeleteValue|DeleteSubKey|DeleteSubKeyTree)$') {
                # The user's registry, reached through Microsoft.Win32.Registry.
                $chain = $m.Extent.Text
                $viaVar = $false
                foreach ($v in $m.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                    if ($cuVars.Contains($v.VariablePath.UserPath)) { $viaVar = $true; break }
                }
                if ($viaVar -or $chain -match '(?i)CurrentUser|HKEY_CURRENT_USER') { $writes.Add($m) }
            }
        }
        # A write is covered when it sits inside a helper's scriptblock, when
        # Assert-WinttySessionStateFree precedes it in the same function (or
        # the same top level), or, inside a function, when that function is
        # called at least once and every call is itself covered. A helper
        # called somewhere else in the file covers nothing.
        $functions = @{}
        foreach ($fd in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { $functions[$fd.Name] = $fd }
        function Covered($node, [int]$Depth) {
            if (InsideHelperBlock $node) { return $true }
            if (PrecededBy $node 'Assert-WinttySessionStateFree' $commands) { return $true }
            $fn = EnclosingFunction $node
            if ($null -eq $fn -or $Depth -ge 4) { return $false }
            $calls = @($commands | Where-Object { $_.GetCommandName() -eq $fn.Name })
            if ($calls.Count -eq 0) { return $false }
            foreach ($call in $calls) { if (-not (Covered $call ($Depth + 1))) { return $false } }
            return $true
        }
        foreach ($w in $writes) {
            if ($reasoned) { continue }
            if (Covered $w 0) { continue }
            $failed.Add("$($file.Name):$($w.Extent.StartLineNumber) changes session-wide state without the session-state helpers (Assert-WinttySessionStateFree / Invoke-WinttySessionStateChange)")
        }

        $code = ($tokens | Where-Object { $_.Kind -ne 'Comment' } | ForEach-Object { $_.Text }) -join ' '
        if ($code -match '(?i)LOCALAPPDATA[^\r\n]{0,60}Wintty[^\r\n]{0,20}crash\.log') {
            $failed.Add("$($file.Name) reads the per-user crash.log; read the session's own (Test-SeamCrashLogWritten)")
        }
    }
    if (Test-Path -LiteralPath $JustfilePath) {
        $text = [System.IO.File]::ReadAllText($JustfilePath)
        if ($text -match '(?m)^\s+\$p = @\(Get-Process Wintty') {
            $failed.Add('the justfile pre-build gate refuses any running Wintty')
        }
        if ($text -notmatch '(?m)^_no-wintty-from exe=.*:\r?\n\s+.*Assert-NoWinttyFrom') {
            $failed.Add('the justfile pre-build gate does not gate on the exe the recipes build')
        }
    }
    return , $failed
}

# The scan's own teeth: a copy of the scripts with one harness changed must
# turn that harness red. Each row names the file and the change.
function Invoke-GateScanMutations {
    $launch = 'launches the exe'; $crash = 'reads the per-user crash.log'; $state = 'changes session-wide state'
    $rows = @(
        @{ Name = 'the splash race launches without its blanket refusal'; File = 'splash-single-instance-race.ps1'; Expect = $launch
           From = "Assert-NoWintty -Context 'The splash race' -Reason ("; To = '$null = (' },
        @{ Name = 'the cdb capture launches through a debugger without its refusal'; File = 'seam-cdb.ps1'; Expect = $launch
           From = "Assert-NoWintty -Context 'The cdb capture' -Reason ("; To = '$null = (' },
        @{ Name = 'the shader fuzz launches without the guard'; File = 'shader-notice-fuzz.ps1'; Expect = $launch
           From = 'Assert-WinttyCoexistence'; To = 'Write-Output' },
        @{ Name = 'a new unguarded launch in a file that guards its other one'; File = 'shader-notice-fuzz.ps1'; Expect = $launch
           Add = 'Start-Process -FilePath $ExePath' },
        @{ Name = 'a call-operator launch of the exe'; File = 'mouse-fuzz-probe.ps1'; Expect = $launch
           Add = '& $ExePath' },
        @{ Name = 'a harness reads the per-user crash.log again'; File = 'mouse-fuzz-probe.ps1'; Expect = $crash
           Add = '$crashPath = "$env:LOCALAPPDATA\Wintty\crash.log"' },
        @{ Name = 'a harness reads the per-user crash.log through nested Join-Path'; File = 'mouse-fuzz-probe.ps1'; Expect = $crash
           Add = '$crashPath = Join-Path (Join-Path $env:LOCALAPPDATA ''Wintty'') ''crash.log''' },
        @{ Name = 'a harness turns animations off outside the helpers'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = 'Set-SpiUint ([uint32]0x1043) ([uint32]0)' },
        @{ Name = 'a harness writes the HKCU theme key outside the helpers'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = "Set-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -Value 0" },
        @{ Name = 'reg.exe adds an HKCU value'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = 'reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v AppsUseLightTheme /t REG_DWORD /d 0 /f' },
        @{ Name = 'Microsoft.Win32.Registry sets an HKCU value'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = "[Microsoft.Win32.Registry]::SetValue('HKEY_CURRENT_USER\Software\x', 'v', 1)" },
        @{ Name = 'a RegistryKey from CurrentUser sets a value'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = "`$k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\x', `$true)`n`$k.SetValue('v', 1)" },
        @{ Name = 'clip.exe writes the clipboard'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = "'x' | clip.exe" },
        @{ Name = 'the scb alias writes the clipboard'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = "scb 'x'" },
        @{ Name = 'a numeric SPI set through a P/Invoke wrapper'; File = 'mouse-fuzz-probe.ps1'; Expect = $state
           Add = '[void][Foo.Native]::SystemParametersInfo(0x1043, 0, [IntPtr]::Zero, 3)' },
        @{ Name = 'a write in a function nothing covers, in a file that uses a helper elsewhere'; File = 'mouse-fuzz-paste-payloads.ps1'; Expect = $state
           Add = 'function Turn-AnimationsOff { Set-SpiUint ([uint32]0x1043) ([uint32]0) }' },
        @{ Name = 'the theme matrix flips the desktop without its refusal'; File = 'theme-matrix.ps1'; Expect = $state
           From = 'Assert-WinttySessionStateFree -ExePath'; To = 'Write-Output -InputObject' },
        @{ Name = 'the fuzz suite clears the desktop outside the helper'; File = 'fuzz-suite.ps1'; Expect = $state
           From = 'if (Invoke-WinttySessionStateChange'; To = 'if (Write-Output' }
    )
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-gatescan-" + [guid]::NewGuid().ToString('N'))
    try {
        $i = 0
        foreach ($row in $rows) {
            $i++
            $dir = Join-Path $root "row-$i"
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            Copy-Item -Path (Join-Path $PSScriptRoot '*.ps1') -Destination $dir
            $target = Join-Path $dir $row.File
            $text = [System.IO.File]::ReadAllText($target)
            if ($row.From) {
                if (-not $text.Contains($row.From)) { Assert-True $false "gate-scan mutation '$($row.Name)': its anchor is gone"; continue }
                $text = $text.Replace($row.From, $row.To)
            }
            if ($row.Add) { $text = $text + "`n" + $row.Add + "`n" }
            [System.IO.File]::WriteAllText($target, $text)
            $red = Invoke-GateScanCases -ScriptDir $dir -JustfilePath (Join-Path $PSScriptRoot '..\..\justfile')
            Assert-True (@($red | Where-Object { $_ -like "$($row.File)*" -and $_ -like "*$($row.Expect)*" }).Count -gt 0) "gate-scan mutation went red for its own rule: $($row.Name)"
        }
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---- layer 8: the birth harness's teardown forwarding -------------------------

# The birth-coverage harness starts two seam sessions and tears both of them
# down from a finally, so a caller's hook can only reach it as a parameter.
# A hook belongs to a launch and never to a Stop (Stop appends, so handing it
# to both runs it twice for one launch), and a hook that throws must be
# recorded against the run rather than escaping the finally, where it would
# abort the script. The harness launches the real app, so none of that can be
# asserted by running it here: the scan reads the file instead.
function Invoke-BirthForwardCases([string]$HarnessPath) {
    $failed = [System.Collections.Generic.List[string]]::new()
    $file = [System.IO.Path]::GetFileName($HarnessPath)
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($HarnessPath, [ref]$tokens, [ref]$errors)
    $commands = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))

    # Typed, not just present: an untyped parameter would take a path or a
    # string of script text and hand it to the seam as a hook.
    $declared = @($ast.ParamBlock.Parameters | Where-Object {
            $_.Name.VariablePath.UserPath -eq 'BeforeTeardown' -and $_.StaticType -eq [scriptblock] })
    if ($declared.Count -eq 0) {
        $failed.Add("$($file) declares no [scriptblock]`$BeforeTeardown parameter")
    }

    $starts = @($commands | Where-Object { $_.GetCommandName() -eq 'Start-SeamSession' })
    # A scan that found no session would be green for the wrong reason.
    if ($starts.Count -eq 0) {
        $failed.Add("$($file) starts no seam session: the scan found nothing to check")
    }
    foreach ($call in $starts) {
        $forwards = $false
        $els = @($call.CommandElements)
        for ($i = 0; $i -lt ($els.Count - 1); $i++) {
            if ($els[$i] -isnot [System.Management.Automation.Language.CommandParameterAst] -or
                $els[$i].ParameterName -ne 'BeforeTeardown') { continue }
            $value = $els[$i + 1]
            if ($value -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $value.VariablePath.UserPath -eq 'BeforeTeardown') { $forwards = $true }
            break
        }
        if (-not $forwards) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) starts a seam session without -BeforeTeardown `$BeforeTeardown")
        }
    }

    foreach ($call in @($commands | Where-Object { $_.GetCommandName() -eq 'Stop-SeamSession' })) {
        if (@($call.CommandElements | Where-Object {
                    $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $_.ParameterName -eq 'BeforeTeardown' }).Count -gt 0) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) passes -BeforeTeardown to Stop-SeamSession (the hook would run twice)")
        }
        # Inside the body of a try that has a catch. A Stop left bare in a
        # finally throws a hook failure out of the run that was recording it.
        $guarded = $false
        $node = $call.Parent
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.TryStatementAst]) {
                $body = $node.Body
                if ($null -ne $body -and $node.CatchClauses.Count -gt 0 -and
                    $body.Extent.StartOffset -le $call.Extent.StartOffset -and
                    $call.Extent.EndOffset -le $body.Extent.EndOffset) { $guarded = $true }
                break
            }
            $node = $node.Parent
        }
        if (-not $guarded) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) calls Stop-SeamSession outside a try/catch (a hook failure would abort the run)")
        }
    }
    return , $failed
}

# ---- layer 8b: the resume harness's contract -----------------------------------

# The session-resumption sibling (seam-session-resume.ps1) runs a different
# lifecycle from the birth harness -- window-save-state=always plus a FRESH
# owned tree per pair (a save and its restore share one tree, no pair sees
# another's session.json) -- and pins the hot path as a loud harness gap
# while the daemon is inert. A scan that found no session, no owned tree, or
# no hot probe would be green for the wrong reason, so each rule below fails
# on absence. What a live run asserts (beyond the scan): the restore shows
# both tabs before opening anything, each pane live with its marker inside
# the cmd budget under the birth oracles; the fallback restore shows both
# tabs with `vanishing` unresolvable; the hot row stays a harness gap until
# the daemon serves pane-sessions.
function Invoke-ResumeCases([string]$HarnessPath) {
    $failed = [System.Collections.Generic.List[string]]::new()
    $file = [System.IO.Path]::GetFileName($HarnessPath)
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($HarnessPath, [ref]$tokens, [ref]$errors)
    $commands = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
    $text = try { [System.IO.File]::ReadAllText($HarnessPath) } catch { '' }

    # Typed, not just present: an untyped parameter would take a path or a
    # string of script text and hand it to the seam as a hook.
    $declared = @($ast.ParamBlock.Parameters | Where-Object {
            $_.Name.VariablePath.UserPath -eq 'BeforeTeardown' -and $_.StaticType -eq [scriptblock] })
    if ($declared.Count -eq 0) {
        $failed.Add("$($file) declares no [scriptblock]`$BeforeTeardown parameter")
    }

    # The coordinator re-runs one path without the other: the selection must
    # offer cold and hot.
    $scen = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Scenario' })
    if ($scen.Count -eq 0) {
        $failed.Add("$($file) declares no `$Scenario selection parameter (cold/hot)")
    }
    elseif ($scen[0].Extent.Text -notmatch 'ValidateSet' -or
            $scen[0].Extent.Text -notmatch "'cold'" -or
            $scen[0].Extent.Text -notmatch "'hot'") {
        $failed.Add("$($file)'s `$Scenario does not offer the cold/hot selection")
    }

    $starts = @($commands | Where-Object { $_.GetCommandName() -eq 'Start-SeamSession' })
    # A scan that found no session would be green for the wrong reason: the
    # resume needs at least a save launch and a restore launch.
    if ($starts.Count -lt 2) {
        $failed.Add("$($file) starts fewer than two seam sessions: no save/restore pair to check")
    }
    foreach ($call in $starts) {
        $forwards = $false
        $els = @($call.CommandElements)
        for ($i = 0; $i -lt ($els.Count - 1); $i++) {
            if ($els[$i] -isnot [System.Management.Automation.Language.CommandParameterAst] -or
                $els[$i].ParameterName -ne 'BeforeTeardown') { continue }
            $value = $els[$i + 1]
            if ($value -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $value.VariablePath.UserPath -eq 'BeforeTeardown') { $forwards = $true }
            break
        }
        if (-not $forwards) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) starts a seam session without -BeforeTeardown `$BeforeTeardown")
        }
    }
    foreach ($call in $starts) {
        # Owned, not private: the switch mints a fresh tree even over an
        # owned one (lib/seam-client.ps1), so the restore would boot fresh.
        if (@($call.CommandElements | Where-Object {
                    $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $_.ParameterName -eq 'PrivateStateBase' }).Count -gt 0) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) starts a seam session with -PrivateStateBase: it mints a fresh tree even over the owned one, so the restore boots fresh")
        }
    }

    foreach ($call in @($commands | Where-Object { $_.GetCommandName() -eq 'Stop-SeamSession' })) {
        if (@($call.CommandElements | Where-Object {
                    $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $_.ParameterName -eq 'BeforeTeardown' }).Count -gt 0) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) passes -BeforeTeardown to Stop-SeamSession (the hook would run twice)")
        }
        # Inside the body of a try that has a catch. A Stop left bare in a
        # finally throws a hook failure out of the run that was recording it.
        $guarded = $false
        $node = $call.Parent
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.TryStatementAst]) {
                $body = $node.Body
                if ($null -ne $body -and $node.CatchClauses.Count -gt 0 -and
                    $body.Extent.StartOffset -le $call.Extent.StartOffset -and
                    $call.Extent.EndOffset -le $body.Extent.EndOffset) { $guarded = $true }
                break
            }
            $node = $node.Parent
        }
        if (-not $guarded) {
            $failed.Add("$($file):$($call.Extent.StartLineNumber) calls Stop-SeamSession outside a try/catch (a hook failure would abort the run)")
        }
    }

    # The cold lifecycle: the kill in Stop-SeamSession never writes a clean
    # shutdown, so only `always` restores the save's dirty file; and the
    # restore only reads the save when both launches of a pair adopt its
    # fresh owned tree (one tree per pair, never one for the run: with
    # `always` a shared tree hands the next save a restore).
    if ($text -notmatch 'window-save-state = always') {
        $failed.Add("$($file) never stages window-save-state = always: a killed save leaves a dirty file `default` would not restore")
    }
    $mints = @($commands | Where-Object { $_.GetCommandName() -eq 'New-WinttyOwnedStateBase' })
    if ($mints.Count -eq 0) {
        $failed.Add("$($file) mints no owned state tree: the restore cannot read the save's session.json")
    }
    elseif ($mints.Count -lt 2) {
        $failed.Add("$($file) mints only one owned tree for the run: a later pair boots on the earlier pair's session.json and restores instead of saving (each pair needs its own tree)")
    }
    $exports = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true) | Where-Object {
            $_.Left.Extent.Text -eq '$env:WINTTY_STATE_BASE' -and $_.Right.Extent.Text -match '\$OwnedPath|\$owned' })
    if ($exports.Count -eq 0) {
        $failed.Add("$($file) never exports the owned tree to its launches: each session would mint a fresh tree and the restore would be fresh too")
    }

    # The rows live as invocations, not prose: the header names every row, so
    # a bare Contains stays green when the live lines are gone. Pin the
    # parsed commands instead (the same AST shape the forwarding rules use).
    $restores = @($commands | Where-Object { $_.GetCommandName() -eq 'Invoke-RestorePhase' })
    $restoreText = ($restores | ForEach-Object { $_.Extent.Text }) -join "`n"
    if ($restores.Count -eq 0 -or $restoreText -notmatch 'resume-cold') {
        $failed.Add("$($file) invokes no live Invoke-RestorePhase cold row (header prose alone proves nothing)")
    }
    if ($restoreText -notmatch 'resume-cold-fallback') {
        $failed.Add("$($file) invokes no live Invoke-RestorePhase fallback row (header prose alone proves nothing)")
    }
    $scenarios = @($commands | Where-Object { $_.GetCommandName() -eq 'Invoke-Scenario' })
    $scenarioText = ($scenarios | ForEach-Object { $_.Extent.Text }) -join "`n"
    if ($scenarioText -notmatch 'resume-hot@daemon') {
        $failed.Add("$($file) invokes no live Invoke-Scenario hot row (header prose alone proves nothing)")
    }
    # The hot proof owes a daemon handle: without the tier's session op no
    # attach can be staged or proved, and the row must say HARNESS, never go
    # green and never blame the product. Pinned on the live seam call, not
    # the header's backticked mention.
    $seamOps = @($commands | Where-Object { $_.GetCommandName() -eq 'Invoke-SeamCommand' })
    $opText = ($seamOps | ForEach-Object { $_.Extent.Text }) -join "`n"
    if ($opText -notmatch 'pane-sessions') {
        $failed.Add("$($file)'s hot row never probes the pane-sessions op: it cannot tell a held session from a fresh boot")
    }
    elseif ($text -notmatch 'HARNESS[^`"]*pane-sessions|pane-sessions[^`"]*HARNESS') {
        $failed.Add("$($file)'s hot row probes pane-sessions but records no HARNESS gap for the unserved tree")
    }
    # The release-side gate parses these rows; a renamed file or a lost exit
    # split blinds it.
    if (-not $text.Contains('results.json')) {
        $failed.Add("$($file) writes no results.json scenario ledger")
    }
    if ($text -notmatch 'exit 2' -or $text -notmatch 'exit 1') {
        $failed.Add("$($file) lost the product-finding (2) versus broken-harness (1) exit split")
    }
    return , $failed
}

# ---- layer 7: the teardown hook ----------------------------------------------

# Start-SeamSession and Stop-SeamSession for real, with every launch step
# answered by a stand-in, so nothing here starts the app. What the cases pin
# is the hook's contract rather than any rule about what a log says: it runs
# once per session on every path that ends in a teardown (the guard refusing,
# the launch failing, the splash wait timing out, the caller stopping), after
# the process is gone and the environment is back and crash.log is read out,
# while the temp root is still on disk -- and the tree dies afterwards either
# way.
function Invoke-TeardownHookCases([string]$LibPath) {
    return & {
        param($processLib, $lib)
        . $processLib
        try { . $lib } catch {
            $failed = [System.Collections.Generic.List[string]]::new()
            $failed.Add("the library cannot be dot-sourced here: $($_.Exception.Message)")
            return , $failed
        }
        $failed = [System.Collections.Generic.List[string]]::new()
        $temp = [System.IO.Path]::GetTempPath()
        $root = Join-Path $temp ("wintty-seam-hookcase-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        # The exe is an empty file, as layer 5 does: the teardown's sweep
        # matches on this path and a start time, and matches nothing.
        $exe = Join-Path $root 'Wintty.exe'
        [System.IO.File]::WriteAllBytes($exe, [byte[]]@())
        # What each stand-in does in the current case, and what the caller's
        # path looked like when the guard saw it.
        $case = @{ Guard = 'pass'; Start = 'ok'; Ready = 'ok' }
        $cap = @{}
        $seen = [System.Collections.Generic.List[object]]::new()

        function Assert-WinttyCoexistence {
            param($ExePath, $ConfigText, $AumId, $Context)
            $cap.TempXdg = $env:XDG_CONFIG_HOME
            if ($case.Guard -eq 'refuse') { throw 'hook-case refusal' }
            return @{ Running = @() }
        }
        function Start-Process {
            param($FilePath, $PassThru, $WorkingDirectory, $ArgumentList)
            if ($case.Start -eq 'throw') { throw 'hook-case start failure' }
            # An app that has already exited, so the teardown's kill is a
            # no-op and the hook can still read the code.
            return [pscustomobject]@{ HasExited = $true; ExitCode = 7; Id = 0 }
        }
        function Wait-SeamReady {
            param($proc)
            if ($case.Ready -eq 'throw') { throw 'hook-case timeout' }
            return @{ Hwnd64 = 1 }
        }
        function Wait-SeamPipe { param($Token, $Proc, $TimeoutSeconds) return $true }
        function Connect-SeamPipe { param($Token, $TimeoutMs) return [System.IO.MemoryStream]::new() }

        # Two hooks that record what they were handed, whether the tree was still
        # there at call time, and which hook of the case they are. The tag is
        # baked into each body rather than read from a shared variable, since
        # both run inside one teardown and would then see one tag. Output to
        # the pipeline is a hook's own way of reporting a finding.
        function Record-Hook([string]$Tag, [object]$Ctx) {
            $seen.Add([pscustomobject]@{
                Tag   = $Tag
                Ctx   = $Ctx
                Alive = (Test-Path -LiteralPath $Ctx.TempRoot)
            })
        }
        $probeStart = { param($ctx) Record-Hook 'start' $ctx; 'hook-case finding from start' }
        $probeStop = { param($ctx) Record-Hook 'stop' $ctx; 'hook-case finding from stop' }
        # How many times a tagged hook ran, how many saw the tree still
        # standing, and the context the last call was handed.
        function Ran([string]$Tag) { return @($seen | Where-Object { $_.Tag -eq $Tag }).Count }
        function Saw-Alive { return @($seen | Where-Object { $_.Alive }).Count }
        function Last-Ctx { return $seen[$seen.Count - 1].Ctx }

        $saved = @{}
        foreach ($n in 'WINTTY_STATE_BASE', 'WINTTY_STATE_BASE_TOKEN', 'XDG_STATE_HOME', 'XDG_CACHE_HOME') {
            $saved[$n] = [System.Environment]::GetEnvironmentVariable($n)
        }
        try {
            foreach ($n in @($saved.Keys)) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
            $refuse = 'hook-case refusal'
            $throwStart = 'hook-case start failure'
            $throwReady = 'hook-case timeout'
            $throwing = { param($ctx) throw 'boom' }

            # ---- the plain path, one hook given to Start ----
            $seen.Clear()
            $s = $null
            try {
                $s = Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart
                Stop-SeamSession -Session $s
            }
            catch { $failed.Add("success path: Stop-SeamSession threw $_") }
            if ((Ran 'start') -ne 1) {
                $failed.Add("success path: the hook ran $(Ran 'start') times, not once")
            }
            if ((Saw-Alive) -ne 1) {
                $failed.Add('success path: the temp root was already deleted when the hook ran')
            }
            if ($s -and (Test-Path -LiteralPath $s.TempXdg)) {
                $failed.Add('success path: the temp root survived teardown')
            }
            $ctx = Last-Ctx
            if ($ctx) {
                if ($ctx.StateBase -ne $s.StateBase -or $ctx.TempRoot -ne $s.TempXdg) {
                    $failed.Add("success path: the hook got StateBase '$($ctx.StateBase)' / TempRoot '$($ctx.TempRoot)', not the session's")
                }
                if ($ctx.ExitCode -ne 7 -or -not $ctx.Launched) {
                    $failed.Add('success path: the hook did not see the exited app (Launched/ExitCode)')
                }
            }

            # ---- a second Stop on the same session is a no-op for the hook ----
            $seen.Clear()
            $s = $null
            try {
                $s = Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart
                Stop-SeamSession -Session $s
                Stop-SeamSession -Session $s
            }
            catch { $failed.Add("second Stop-SeamSession threw $_") }
            if ((Ran 'start') -ne 1) {
                $failed.Add('a second Stop-SeamSession ran the hook again')
            }

            # ---- the guard refuses: the hook still runs once ----
            $seen.Clear()
            $case.Guard = 'refuse'
            $got = ''
            try { Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart }
            catch { $got = "$_" }
            $case.Guard = 'pass'
            if ($got -notmatch [regex]::Escape($refuse)) {
                $failed.Add("guard refusal: the caller did not see the guard's own error ('$got')")
            }
            if ((Ran 'start') -ne 1) {
                $failed.Add("guard refusal: the hook ran $(Ran 'start') times, not once")
            }

            # ---- Start-Process itself throws: nothing leaks, hook once ----
            $seen.Clear()
            $case.Start = 'throw'
            $got = ''
            $leaked = ''
            try { Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart }
            catch { $got = "$_" }
            $case.Start = 'ok'
            if ($got -notmatch [regex]::Escape($throwStart)) {
                $failed.Add("launch failure: the caller did not see the launch's own error ('$got')")
            }
            if ((Ran 'start') -ne 1) {
                $failed.Add("launch failure: the hook ran $(Ran 'start') times, not once")
            }
            if ($cap.TempXdg -and (Test-Path -LiteralPath $cap.TempXdg)) { $leaked = $cap.TempXdg }
            if ($leaked) { $failed.Add("launch failure: the temp root was left behind ('$leaked')") }

            # ---- the splash wait times out: hook once ----
            $seen.Clear()
            $case.Ready = 'throw'
            $got = ''
            try { Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart }
            catch { $got = "$_" }
            $case.Ready = 'ok'
            if ($got -notmatch [regex]::Escape($throwReady)) {
                $failed.Add("startup timeout: the caller did not see the startup error ('$got')")
            }
            if ((Ran 'start') -ne 1) {
                $failed.Add("startup timeout: the hook ran $(Ran 'start') times, not once")
            }

            # ---- a throwing hook fails the teardown, and the tree still goes ----
            $seen.Clear()
            $s = $null
            $got = ''
            try {
                $s = Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $throwing
                Stop-SeamSession -Session $s
            }
            catch { $got = "$_" }
            if (-not $got) {
                $failed.Add('a throwing hook did not fail Stop-SeamSession')
            }
            elseif ($got -notmatch 'the BeforeTeardown hook failed: boom') {
                $failed.Add("a throwing hook's error did not name it: '$got'")
            }
            if ($s -and (Test-Path -LiteralPath $s.TempXdg)) {
                $failed.Add('a throwing hook left the temp root behind')
            }

            # ---- a throwing hook must not hide the error it rode in on ----
            $seen.Clear()
            $case.Ready = 'throw'
            $got = ''
            try { Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $throwing }
            catch { $got = "$_" }
            $case.Ready = 'ok'
            $atTimeout = $got.IndexOf($throwReady)
            $atBoom = $got.IndexOf('boom')
            if ($atTimeout -lt 0 -or $atBoom -lt 0 -or $atTimeout -gt $atBoom) {
                $failed.Add("a teardown failure hid the startup error: '$got'")
            }

            # ---- what a hook writes reaches the session ----
            $seen.Clear()
            $s = $null
            try {
                $s = Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart
                Stop-SeamSession -Session $s
            }
            catch { }
            if (-not $s -or -not $s.ContainsKey('TeardownFindings') -or
                @($s.TeardownFindings | Where-Object { $_ -match 'hook-case finding' }).Count -eq 0) {
                $failed.Add('the hook''s output did not reach TeardownFindings')
            }

            # ---- Stop's hook runs after Start's ----
            $seen.Clear()
            $s = $null
            try {
                $s = Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never' -BeforeTeardown $probeStart
                Stop-SeamSession -Session $s -BeforeTeardown $probeStop
            }
            catch { $failed.Add("Stop's own hook path threw $_") }
            if ((Ran 'start') -ne 1 -or (Ran 'stop') -ne 1) {
                $failed.Add("Stop-SeamSession -BeforeTeardown did not run after Start's hook")
            }

            # ---- no hook at all: the pre-existing behaviour ----
            $seen.Clear()
            $s = $null
            try {
                $s = Start-SeamSession -ExePath $exe -ConfigText 'window-save-state = never'
                Stop-SeamSession -Session $s
            }
            catch { $failed.Add("no hook: Stop-SeamSession threw $_") }
            if ($s -and (Test-Path -LiteralPath $s.TempXdg)) {
                $failed.Add('no hook: the temp root survived teardown')
            }
        }
        finally {
            foreach ($n in @($saved.Keys)) { if ($null -ne $saved[$n]) { Set-Item "Env:$n" $saved[$n] } else { Remove-Item "Env:$n" -ErrorAction SilentlyContinue } }
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
        return , $failed
    } $script:processLib $LibPath
}

# ---- layer 8: the Start-Process harnesses' state tree (lib/test-config.ps1) -----

# Enter-WinttyTestConfig -PrivateStateBase for real: the tree and the
# native-state dirs land under the random root, a crash.log written there is
# still reported after Exit removed the root, and every variable is back as
# the caller had it. Nothing is launched.
function Invoke-TestConfigCases([string]$TestConfigPath) {
    return & {
        param($processLib, $lib)
        . $processLib
        $failed = [System.Collections.Generic.List[string]]::new()
        try { . $lib } catch {
            $failed.Add("the library cannot be dot-sourced here: $($_.Exception.Message)")
            return , $failed
        }
        $names = 'XDG_CONFIG_HOME', 'WINTTY_TEST_CONFIG', 'WINTTY_STATE_BASE', 'XDG_STATE_HOME', 'XDG_CACHE_HOME'
        $saved = @{}
        foreach ($n in $names) { $saved[$n] = if (Test-Path "Env:$n") { (Get-Item "Env:$n").Value } else { $null } }
        try {
            # A caller value for each, so the restore is visible.
            foreach ($n in $names) { Set-Item "Env:$n" "caller-$n" }
            $s = Enter-WinttyTestConfig -PrivateStateBase
            if (-not $s.StateBase -or -not (Test-WinttyPathUnder $s.StateBase $s.Dir)) {
                $failed.Add("-PrivateStateBase did not put the state tree under the random root (saw '$($s.StateBase)')")
            }
            if ($env:WINTTY_STATE_BASE -ne $s.StateBase) { $failed.Add('-PrivateStateBase did not export WINTTY_STATE_BASE') }
            foreach ($n in 'XDG_STATE_HOME', 'XDG_CACHE_HOME') {
                $v = (Get-Item "Env:$n" -ErrorAction SilentlyContinue).Value
                if (-not $v -or -not $s.StateBase -or -not (Test-WinttyPathUnder $v $s.StateBase)) {
                    $failed.Add("-PrivateStateBase did not put $n inside the state tree (saw '$v')")
                }
            }
            $dir = Join-Path $s.StateBase 'Wintty'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $dir 'crash.log'), 'test-config crash')
            if (@(Get-WinttyTestConfigCrashLogs $s).Count -ne 1) { $failed.Add('a crash.log in the live tree is not reported') }
            Exit-WinttyTestConfig $s
            if (Test-Path -LiteralPath $s.Dir) { $failed.Add('Exit did not remove the random root') }
            $after = @(Get-WinttyTestConfigCrashLogs $s)
            if ($after.Count -ne 1 -or $after[0].Text -ne 'test-config crash') {
                $failed.Add('a crash.log written before Exit is not reported after Exit removed the tree')
            }
            foreach ($n in $names) {
                $v = (Get-Item "Env:$n" -ErrorAction SilentlyContinue).Value
                if ($v -ne "caller-$n") { $failed.Add("Exit did not restore $n (saw '$v')") }
            }

            # Without -PrivateStateBase the state variables are not touched.
            $s = Enter-WinttyTestConfig
            if ($env:WINTTY_STATE_BASE -ne 'caller-WINTTY_STATE_BASE' -or $s.StateBase) {
                $failed.Add('Enter without -PrivateStateBase moved the state tree')
            }
            Exit-WinttyTestConfig $s
        }
        finally {
            foreach ($n in $names) {
                if ($null -ne $saved[$n]) { Set-Item "Env:$n" $saved[$n] } else { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
            }
        }
        return , $failed
    } $script:processLib $TestConfigPath
}

# ---- run the layers -----------------------------------------------------------

$minterFailures = Invoke-MinterCases $SeamClientPath
foreach ($f in $minterFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($minterFailures.Count -eq 0) 'every minter case holds'

$guardFailures = Invoke-GuardCases $SeamClientPath
foreach ($f in $guardFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($guardFailures.Count -eq 0) 'every guard case holds against the real library'

$wiringFailures = Invoke-WiringCases $SeamClientPath
foreach ($f in $wiringFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($wiringFailures.Count -eq 0) 'every wiring case holds against the real library'

$stateFailures = Invoke-StateBaseCases $SeamClientPath
foreach ($f in $stateFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($stateFailures.Count -eq 0) 'every state-base and crash-oracle case holds against the real library'

$hookFailures = Invoke-TeardownHookCases $SeamClientPath
foreach ($f in $hookFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($hookFailures.Count -eq 0) 'the teardown hook runs once on every exit path, before the tree is deleted'

$gateFailures = Invoke-GateScanCases
foreach ($f in $gateFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($gateFailures.Count -eq 0) 'every harness gates on the exe under test and reads its own crash.log'
Invoke-GateScanMutations

$birthHarness = Join-Path $PSScriptRoot 'seam-initial-size.ps1'
$birthFailures = Invoke-BirthForwardCases $birthHarness
foreach ($f in $birthFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($birthFailures.Count -eq 0) 'the birth harness forwards -BeforeTeardown to every seam session and records a teardown failure'

# Every mutation engine below feeds its row's Break the harness one string
# per line and takes lines back the same way. The invocation this engine was
# born with, `@(& $row.Break (, $lines))`, binds the whole array as ONE item:
# a filter row then ran Where-Object once over the file-as-object, its match
# operator filtered an array inside a single truthiness test, and
# WriteAllLines wrote the stringified file as one unparseable line. Such a
# row went red on the parse errors of a mutant that carried no edit at all,
# so the red proved nothing about the rule the row pins. Bind the lines flat
# and flatten what comes back once: a row may return a filtered pipeline, a
# bare list (the Move-* helpers) or a wrapped one (`return , $out`), and all
# three have to write a line-per-line file.
function Invoke-RowBreak([scriptblock]$Break, [string[]]$Lines) {
    [string[]](@(& $Break $Lines) | ForEach-Object { $_ })
}

# A block ends at the first line that closes the indentation its own braces
# opened at, which is what the file's 4-space style means. Shared by the
# moved-statement rows below and by the rows that un-guard a Stop.
function Get-BlockEnd([string[]]$Lines, [int]$Start) {
    $indent = ([regex]::Match($Lines[$Start], '^(\s*)')).Groups[1].Value
    for ($i = $Start + 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -ceq ($indent + '}')) { return $i }
    }
    return -1
}

# The scan's own teeth: a copy of the birth harness with one rule broken has
# to turn the scan red. Each row edits that file's lines, and a row whose
# edit changed nothing says so instead of scanning a copy of the harness.
$birthMutantRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-birth-mutants-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $birthMutantRoot | Out-Null
try {
    $birthLines = [System.IO.File]::ReadAllLines($birthHarness)
    $birthMutant = Join-Path $birthMutantRoot ([System.IO.Path]::GetFileName($birthHarness))
    $birthRows = @(
        @{
            Name = 'the parameter is gone'
            Break = {
                param($lines)
                # The parameter is the block's last, so the comma on the
                # parameter above it goes too -- past the block's comments --
                # because the mutant has to still parse.
                $out = [System.Collections.Generic.List[string]]::new()
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($lines[$i] -match '^\s*\[scriptblock\]\$BeforeTeardown = \$null\s*$') {
                        if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s*\)') {
                            for ($j = $out.Count - 1; $j -ge 0; $j--) {
                                if ($out[$j] -match '^\s*#') { continue }
                                $out[$j] = $out[$j] -replace ',\s*$', ''
                                break
                            }
                        }
                        continue
                    }
                    $out.Add($lines[$i])
                }
                return , $out
            }
        },
        @{
            Name = 'one start does not forward'
            Break = {
                param($lines)
                # The second launch only: breaking the first would fail the
                # same rule and the row would prove nothing of its own.
                $out = [System.Collections.Generic.List[string]]::new()
                $seen = 0
                foreach ($line in $lines) {
                    if ($line -match '^\s*\$s = Start-SeamSession ' -and (++$seen) -eq 2) {
                        $out.Add($line.Replace(' -BeforeTeardown $BeforeTeardown', ''))
                    }
                    else { $out.Add($line) }
                }
                return , $out
            }
        },
        @{
            Name = 'Stop also gets the hook'
            Break = {
                param($lines)
                $out = [System.Collections.Generic.List[string]]::new()
                $done = $false
                foreach ($line in $lines) {
                    if (-not $done -and $line -match '^\s*Stop-SeamSession \$s\b') {
                        $done = $true
                        $out.Add($line + ' -BeforeTeardown $BeforeTeardown')
                    }
                    else { $out.Add($line) }
                }
                return , $out
            }
        },
        @{
            Name = 'a Stop outside try/catch'
            Break = {
                param($lines)
                # The first wrapped Stop, un-wrapped: its try line and the
                # brace that closed it go, and the catch becomes a plain
                # block, so the call is bare in the finally again, a hook
                # failure would abort the run, and the mutant still parses.
                $stop = -1
                for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*Stop-SeamSession \$s\b') { $stop = $i; break } }
                $try = -1
                for ($i = $stop - 1; $i -ge 0; $i--) { if ($lines[$i] -match '^\s*try \{$') { $try = $i; break } }
                $catch = -1
                for ($i = $stop + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*catch \{$') { $catch = $i; break } }
                $close = Get-BlockEnd $lines $try
                $out = [System.Collections.Generic.List[string]]::new()
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($i -eq $try -or $i -eq $close) { continue }
                    if ($i -eq $catch) { $out.Add($lines[$i].Replace('catch {', 'if ($true) {')); continue }
                    $out.Add($lines[$i])
                }
                return , $out
            }
        }
    )
    foreach ($row in $birthRows) {
        $lines = Invoke-RowBreak $row.Break $birthLines
        if (($lines -join "`n") -ceq ($birthLines -join "`n")) {
            Assert-True $false "birth mutation '$($row.Name)': its anchor is gone"
            continue
        }
        [System.IO.File]::WriteAllLines($birthMutant, [string[]]$lines)
        Assert-True ((Invoke-BirthForwardCases $birthMutant).Count -gt 0) "birth mutation went red: $($row.Name)"
    }
}
finally { Remove-Item -LiteralPath $birthMutantRoot -Recurse -Force -ErrorAction SilentlyContinue }

$resumeHarness = Join-Path $PSScriptRoot 'seam-session-resume.ps1'
$resumeFailures = Invoke-ResumeCases $resumeHarness
foreach ($f in $resumeFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($resumeFailures.Count -eq 0) 'the resume harness keeps the cold lifecycle, the hook forwarding and the hot pin'

# The scan's own teeth: a copy of the resume harness with one rule broken has
# to turn the scan red. Each row edits that file's lines, and a row whose
# edit changed nothing says so instead of scanning a copy of the harness.
$resumeMutantRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-resume-mutants-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $resumeMutantRoot | Out-Null
try {
    $resumeLines = [System.IO.File]::ReadAllLines($resumeHarness)
    $resumeMutant = Join-Path $resumeMutantRoot ([System.IO.Path]::GetFileName($resumeHarness))
    $resumeRows = @(
        @{
            Name = 'the always policy is gone'
            Break = {
                param($lines)
                @($lines | ForEach-Object { $_.Replace('window-save-state = always', 'window-save-state = never') })
            }
        },
        @{
            Name = 'the owned trees are gone'
            Break = {
                param($lines)
                @($lines | Where-Object { $_ -notmatch 'New-WinttyOwnedStateBase' })
            }
        },
        @{
            Name = 'the Scenario selection is gone'
            Break = {
                param($lines)
                @($lines | Where-Object { $_ -notmatch "ValidateSet\('all', 'cold', 'hot'\)" })
            }
        },
        @{
            Name = 'one start does not forward'
            Break = {
                param($lines)
                # The second launch only: breaking the first would fail the
                # same rule and the row would prove nothing of its own.
                $out = [System.Collections.Generic.List[string]]::new()
                $seen = 0
                foreach ($line in $lines) {
                    if ($line -match '^\s*\$s = Start-SeamSession ' -and (++$seen) -eq 2) {
                        $out.Add($line.Replace(' -BeforeTeardown $BeforeTeardown', ''))
                    }
                    else { $out.Add($line) }
                }
                return , $out
            }
        },
        @{
            Name = 'Stop also gets the hook'
            Break = {
                param($lines)
                $out = [System.Collections.Generic.List[string]]::new()
                $done = $false
                foreach ($line in $lines) {
                    if (-not $done -and $line -match '^\s*Stop-SeamSession \$s\b') {
                        $done = $true
                        $out.Add($line + ' -BeforeTeardown $BeforeTeardown')
                    }
                    else { $out.Add($line) }
                }
                return , $out
            }
        },
        @{
            Name = 'a Stop outside try/catch'
            Break = {
                param($lines)
                # The first wrapped Stop, un-wrapped: its try line and the
                # brace that closed it go, and the catch becomes a plain
                # block, so the call is bare in the finally again, a hook
                # failure would abort the run, and the mutant still parses.
                $stop = -1
                for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*Stop-SeamSession \$s\b') { $stop = $i; break } }
                $try = -1
                for ($i = $stop - 1; $i -ge 0; $i--) { if ($lines[$i] -match '^\s*try \{$') { $try = $i; break } }
                $catch = -1
                for ($i = $stop + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*catch \{$') { $catch = $i; break } }
                $close = Get-BlockEnd $lines $try
                $out = [System.Collections.Generic.List[string]]::new()
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($i -eq $try -or $i -eq $close) { continue }
                    if ($i -eq $catch) { $out.Add($lines[$i].Replace('catch {', 'if ($true) {')); continue }
                    $out.Add($lines[$i])
                }
                return , $out
            }
        },
        @{
            Name = 'the hot probe is gone'
            Break = {
                param($lines)
                @($lines | Where-Object { $_ -notmatch 'pane-sessions' })
            }
        },
        @{
            Name = 'a start mints private over the owned tree'
            Break = {
                param($lines)
                # One switch back on the first launch: the owned tree is
                # adopted only without it, so the scan must go red. The same
                # AST-ish anchor the forwarding row uses.
                $out = [System.Collections.Generic.List[string]]::new()
                $done = $false
                foreach ($line in $lines) {
                    if (-not $done -and $line -match '^\s*\$s = Start-SeamSession ' -and $line -notmatch 'PrivateStateBase') {
                        $done = $true
                        $out.Add($line.Replace(' -BeforeTeardown $BeforeTeardown', ' -PrivateStateBase -BeforeTeardown $BeforeTeardown'))
                    }
                    else { $out.Add($line) }
                }
                return , $out
            }
        },
        @{
            Name = 'the live cold rows are gone (header prose remains)'
            Break = {
                param($lines)
                # The header names every row, so only the live invocations
                # go: a prose pin would stay green over this.
                @($lines | Where-Object { $_ -notmatch '^\s*Invoke-RestorePhase\s' })
            }
        }
    )
    foreach ($row in $resumeRows) {
        $lines = Invoke-RowBreak $row.Break $resumeLines
        if (($lines -join "`n") -ceq ($resumeLines -join "`n")) {
            Assert-True $false "resume mutation '$($row.Name)': its anchor is gone"
            continue
        }
        [System.IO.File]::WriteAllLines($resumeMutant, [string[]]$lines)
        Assert-True ((Invoke-ResumeCases $resumeMutant).Count -gt 0) "resume mutation went red: $($row.Name)"
    }
}
finally { Remove-Item -LiteralPath $resumeMutantRoot -Recurse -Force -ErrorAction SilentlyContinue }

$script:testConfigLib = Join-Path $PSScriptRoot 'lib/test-config.ps1'
$testConfigFailures = Invoke-TestConfigCases $script:testConfigLib
foreach ($f in $testConfigFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($testConfigFailures.Count -eq 0) 'every test-config state-tree case holds against the real library'

# The one rule of that library whose loss would blind three harnesses at
# once: Exit reads the crash.log out before it removes the root.
$tcMutant = Join-Path ([System.IO.Path]::GetTempPath()) ('wintty-testconfig-mutant-' + [guid]::NewGuid().ToString('N') + '.ps1')
try {
    $src = [System.IO.File]::ReadAllText($script:testConfigLib)
    $anchor = 'if (-not $Session.ContainsKey(''CrashLogs'')) { $Session.CrashLogs = @(Get-WinttyTestConfigCrashLogs $Session) }'
    Assert-True $src.Contains($anchor) 'test-config mutation anchor (Exit''s crash read-out) is present'
    [System.IO.File]::WriteAllText($tcMutant, $src.Replace($anchor, ''))
    Assert-True ((Invoke-TestConfigCases $tcMutant).Count -gt 0) 'mutation went red: Exit no longer reads the crash.log out before removing the root'
}
finally { Remove-Item -LiteralPath $tcMutant -Force -ErrorAction SilentlyContinue }

# ---- layer 4: mutation rows ---------------------------------------------------

# A copy of the library with one rule broken. Every row must turn at least
# one layer red; a row that stays green means the cases no longer pin that
# rule.
#
# Two of the rows need a statement moved rather than a word replaced: the
# hook block out from above the delete and below it, and the launch out of
# the try that tears it down. Both are edits over the file's lines, and both
# hand the lines back flat, which is what WriteAllLines below wants.
# Get-BlockEnd, which the moved-statement rows and the two un-try/catch rows
# share, sits with the other engine helpers above the first engine.
function Move-LinesAfter([string[]]$Lines, [string]$Open, [string]$After) {
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match $Open) { $start = $i; break } }
    $end = if ($start -lt 0) { -1 } else { Get-BlockEnd $Lines $start }
    $at = -1
    if ($end -ge 0) {
        for ($i = $end + 1; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match $After) { $at = $i; break } }
    }
    # An anchor that is not there is the row's business, not a crash here.
    if ($end -lt 0 -or $at -lt 0) { return $Lines }
    $out = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($i -lt $start -or $i -gt $end) { $out.Add($Lines[$i]) }
        if ($i -eq $at) { $out.AddRange([string[]]$Lines[$start..$end]) }
    }
    return $out
}
function Move-LinesBefore([string[]]$Lines, [string]$Move, [string]$Before) {
    $at = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match $Move) { $at = $i; break } }
    $to = -1
    if ($at -ge 0) {
        for ($i = $at; $i -ge 0; $i--) { if ($Lines[$i] -match $Before) { $to = $i; break } }
    }
    if ($at -lt 0 -or $to -lt 0) { return $Lines }
    $out = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($i -eq $to) { $out.Add($Lines[$at]) }
        if ($i -ne $at) { $out.Add($Lines[$i]) }
    }
    return $out
}
$mutantRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-seam-mutants-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $mutantRoot | Out-Null
try {
    $rows = @(
        @{
            Name    = 'mint dropped from the launch environment'
            Break   = { param($lines) @($lines | Where-Object { $_ -notmatch '^\s*\$env:WINTTY_SESSIOND_PIPE\s*=' }) }
            Layer   = 'wiring'
        },
        @{
            Name    = 'per-user spelling in the minter'
            Break   = { param($lines) @($lines | ForEach-Object { $_.Replace('winttyd-test-', 'winttyd-Vendor.Wintty.User-S-1-5-21-11-22-33-1001-') }) }
            Layer   = 'minter'
        },
        @{
            Name    = 'the pipe is not restored'
            Break   = { param($lines) @($lines | Where-Object { $_ -notmatch '^\s*if \(\$null -ne \$Session\.OrigSessionPipe\) ' -and $_ -notmatch '^\s*else \{ Remove-Item Env:WINTTY_SESSIOND_PIPE ' }) }
            Layer   = 'wiring'
        },
        @{
            Name    = 'the private state tree is opt-in again'
            Break   = { param($lines) @($lines | ForEach-Object { $_.Replace('if ($SharedStateBase) {', 'if (-not $PrivateStateBase) {') }) }
            Layer   = 'state'
        },
        @{
            Name    = 'the crash.log is not read out before teardown'
            Break   = { param($lines) @($lines | ForEach-Object { $_.Replace('$Session.CrashLogs = @(Get-SeamCrashLogs $Session)', '$Session.CrashLogs = @()') }) }
            Layer   = 'state'
        },
        @{
            Name    = 'a session is not recorded for the run'
            Break   = { param($lines) @($lines | Where-Object { $_ -notmatch '^\s*\(Get-SeamRunSessions\)\.Add\(\$session\)' }) }
            Layer   = 'state'
        },
        @{
            Name    = 'the mark does not scope the oracle'
            Break   = { param($lines) @($lines | ForEach-Object { $_.Replace('for ($i = [Math]::Max(0, $Since);', 'for ($i = 0;') }) }
            Layer   = 'state'
        },
        @{
            Name    = 'the daemon log file is not named'
            Break   = { param($lines) @($lines | Where-Object { $_ -notmatch '^\s*\$env:WINTTY_SESSIOND_LOG_FILE\s*=' }) }
            Layer   = 'wiring'
        },
        @{
            Name    = 'the hook runs after the tree is deleted'
            Break   = { param($lines) Move-LinesAfter $lines '^(\s*)if \(-not \$Session\.TeardownHookRan\) \{$' '^\s*Remove-Item \$Session\.TempXdg' }
            Layer   = 'hook'
        },
        @{
            Name    = 'the hook runs on every Stop'
            Break   = { param($lines) @($lines | ForEach-Object { $_.Replace('if (-not $Session.TeardownHookRan) {', 'if ($true) {') }) }
            Layer   = 'hook'
        },
        @{
            Name    = 'a throwing hook is swallowed'
            Break   = { param($lines) @($lines | Where-Object { $_ -notmatch 'the BeforeTeardown hook failed:' }) }
            Layer   = 'hook'
        },
        @{
            Name    = 'Start-Process outside the teardown try'
            Break   = { param($lines) Move-LinesBefore $lines '^\s*\$proc = Start-Process @startArgs$' '^\s*try \{$' }
            Layer   = 'hook'
        }
    )
    foreach ($row in $rows) {
        $mutant = Join-Path $mutantRoot ('mutant-' + ($row.Name -replace '[^a-z0-9]+', '-') + '.ps1')
        [System.IO.File]::WriteAllLines($mutant, (Invoke-RowBreak $row.Break ([System.IO.File]::ReadAllLines($SeamClientPath))))
        $wentRed =
            (Invoke-MinterCases $mutant).Count -gt 0 -or
            (Invoke-GuardCases $mutant).Count -gt 0 -or
            (Invoke-WiringCases $mutant).Count -gt 0 -or
            (Invoke-StateBaseCases $mutant).Count -gt 0 -or
            (Invoke-TeardownHookCases $mutant).Count -gt 0
        Assert-True $wentRed ("mutation went red: $($row.Name)")
    }
}
finally {
    Remove-Item $mutantRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:fails -gt 0) {
    Write-Host ("FAILED ({0})" -f $script:fails) -ForegroundColor Red
    exit 1
}
Write-Host 'ALL PASS'
exit 0
