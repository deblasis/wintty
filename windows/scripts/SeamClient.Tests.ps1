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
#      one rule broken. Every row must turn something red.
#
# Nothing here launches Wintty: the guard cases inject stand-in instances,
# and the wiring cases read the library's text.
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
            (Invoke-WiringCases $mutant).Count -gt 0
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
