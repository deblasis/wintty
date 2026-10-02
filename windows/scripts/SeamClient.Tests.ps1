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
#      on any running Wintty, and none reads the per-user crash.log.
#
# Nothing here launches Wintty: the guard cases inject stand-in instances,
# the wiring cases read the library's text, and the state-base cases stop
# at a stand-in guard that refuses before anything starts.
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
# removes, which has to say it. And no harness reads the per-user crash.log,
# which every other Wintty on the machine writes too.
function Invoke-GateScanCases(
    [string]$ScriptDir = $PSScriptRoot,
    [string]$JustfilePath = (Join-Path $PSScriptRoot '..\..\justfile')
) {
    $failed = [System.Collections.Generic.List[string]]::new()
    # The sanctioned blanket refusals, each with a reason no isolation
    # removes: WER LocalDumps is keyed on the image name, so it covers every
    # running Wintty; the splash race measures the single-instance election
    # itself, so its launches keep it on and share the per-user tree.
    $blanketAllowed = @('seam-crash-dump.ps1', 'splash-single-instance-race.ps1')
    # Scripts that launch the exe themselves without the guard or a blanket
    # refusal. None is run by a just recipe, the fuzz suite or the AOT fuzz,
    # and none ever had a gate this conversion loosened; each is listed so a
    # new one cannot join them silently, and listing is refused once a
    # recipe or a suite runs it.
    # The developer's own launcher (just run-win) is not a harness: it opens
    # the app the way a user does, on purpose.
    $notHarness = @('run-win-launch.ps1')
    $unguardedAllowed = @{
        'mouse-smoke-run.ps1' = 'the operator drives it by hand and quits the app themselves'
        'config-save-race.ps1' = 'a standalone repro, run by hand'
        'crash-canary.ps1'    = 'a standalone crash repro against a NativeAOT publish, run by hand'
        'crash-matrix.ps1'    = 'a standalone crash repro against a NativeAOT publish, run by hand'
        'seam-bisect.ps1'     = 'a bisect driver, run by hand'
        'seam-probe.ps1'      = 'a one-off probe, run by hand'
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

    # A launch of the exe under test: Start-Process or a ProcessStartInfo
    # whose file is an exe variable, which is how every launcher here names
    # the app (pwsh, rundll32 and wsl go in as literals or other names).
    $exeArg = '(?i)^\(?\$(\w+[:.])?\w*exe\w*'
    # A secondary launched from a session's own exe runs inside the
    # environment that session's guard approved.
    $sessionSecondary = '(?i)^\$\w+\.ExePath$'

    foreach ($file in Get-ChildItem -LiteralPath $ScriptDir -Filter *.ps1 -File) {
        if ($file.Name -like '*.Tests.ps1') { continue }
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        $calls = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.GetCommandName() -eq 'Assert-NoWintty' }, $true))
        $reasoned = $false
        foreach ($call in $calls) {
            if ($blanketAllowed -notcontains $file.Name) {
                $failed.Add("$($file.Name):$($call.Extent.StartLineNumber) refuses any running Wintty; gate on the exe under test (Assert-NoWinttyFrom)")
            }
            elseif ($call.Extent.Text -notmatch '-Reason\b') {
                $failed.Add("$($file.Name):$($call.Extent.StartLineNumber) refuses any running Wintty without saying why (-Reason)")
            }
            else { $reasoned = $true }
        }
        $guarded = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.GetCommandName() -eq 'Assert-WinttyCoexistence' }, $true)).Count -gt 0
        $usesSession = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.GetCommandName() -eq 'Start-SeamSession' }, $true)).Count -gt 0

        $launches = [System.Collections.Generic.List[string]]::new()
        foreach ($c in $ast.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.CommandAst] -and
                    $n.GetCommandName() -eq 'Start-Process' }, $true)) {
            $els = $c.CommandElements
            # -FilePath by name, else the first positional element.
            $target = $null
            for ($i = 1; $i -lt $els.Count; $i++) {
                if ($els[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and
                    ($els[$i].ParameterName -like 'FilePath*' -or $els[$i].ParameterName -eq 'Path')) {
                    $target = if ($els[$i].Argument) { $els[$i].Argument } elseif ($i + 1 -lt $els.Count) { $els[$i + 1] }
                    break
                }
            }
            if (-not $target -and $els.Count -gt 1 -and
                $els[1] -isnot [System.Management.Automation.Language.CommandParameterAst]) { $target = $els[1] }
            if ($target -and $target.Extent.Text -match $exeArg) {
                $launches.Add("$($c.Extent.StartLineNumber):$($target.Extent.Text)")
            }
        }
        foreach ($m in $ast.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $n.Member.Extent.Text -eq 'new' -and $n.Expression.Extent.Text -match 'ProcessStartInfo\]$' }, $true)) {
            if ($m.Arguments.Count -gt 0 -and $m.Arguments[0].Extent.Text -match $exeArg) {
                $launches.Add("$($m.Extent.StartLineNumber):$($m.Arguments[0].Extent.Text)")
            }
        }
        $open = @($launches | Where-Object { -not ($usesSession -and ($_ -split ':', 2)[1] -match $sessionSecondary) })
        if ($open.Count -gt 0 -and -not $guarded -and -not $reasoned -and $notHarness -notcontains $file.Name) {
            if ($unguardedAllowed.ContainsKey($file.Name)) {
                if ($reachable.Contains($file.Name)) {
                    $failed.Add("$($file.Name) launches the exe without the coexistence guard and is run by a recipe or a suite now; it can no longer be listed as hand-run")
                }
            }
            else {
                $failed.Add("$($file.Name) launches the exe (line $($open -join ', line ')) without the coexistence guard and without a reasoned Assert-NoWintty")
            }
        }

        $code = ($tokens | Where-Object { $_.Kind -ne 'Comment' } | ForEach-Object { $_.Text }) -join ' '
        if ($code -match '(?i)LOCALAPPDATA[^\r\n]{0,40}Wintty[\\/]+crash\.log') {
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

# The scan's own teeth: a copy of the scripts with one harness's guard taken
# out must turn it red. Each row names the file and what to remove.
function Invoke-GateScanMutations {
    $rows = @(
        @{ Name = 'the splash race launches without its blanket refusal'; File = 'splash-single-instance-race.ps1'
           From = "Assert-NoWintty -Context 'The splash race' -Reason ("; To = '$null = (' },
        @{ Name = 'the shader fuzz launches without the guard'; File = 'shader-notice-fuzz.ps1'
           From = 'Assert-WinttyCoexistence'; To = 'Write-Output' },
        @{ Name = 'a harness reads the per-user crash.log again'; File = 'mouse-fuzz-probe.ps1'
           Add = '$crashPath = "$env:LOCALAPPDATA\Wintty\crash.log"' }
    )
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("wintty-gatescan-" + [guid]::NewGuid().ToString('N'))
    try {
        foreach ($row in $rows) {
            $dir = Join-Path $root ($row.File -replace '\W', '-')
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
            Assert-True (@($red | Where-Object { $_ -like "$($row.File)*" }).Count -gt 0) "gate-scan mutation went red: $($row.Name)"
        }
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---- layer 7: the Start-Process harnesses' state tree (lib/test-config.ps1) -----

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

$gateFailures = Invoke-GateScanCases
foreach ($f in $gateFailures) { Write-Host "FAIL: $f" -ForegroundColor Red }
Assert-True ($gateFailures.Count -eq 0) 'every harness gates on the exe under test and reads its own crash.log'
Invoke-GateScanMutations

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
        }
    )
    foreach ($row in $rows) {
        $mutant = Join-Path $mutantRoot ('mutant-' + ($row.Name -replace '[^a-z0-9]+', '-') + '.ps1')
        [System.IO.File]::WriteAllLines($mutant, (@(& $row.Break (, [System.IO.File]::ReadAllLines($SeamClientPath)))))
        $wentRed =
            (Invoke-MinterCases $mutant).Count -gt 0 -or
            (Invoke-GuardCases $mutant).Count -gt 0 -or
            (Invoke-WiringCases $mutant).Count -gt 0 -or
            (Invoke-StateBaseCases $mutant).Count -gt 0
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
