# The isolated-config helper: stage a per-run random config root under
# %TEMP%, point XDG_CONFIG_HOME at it, and arm the app's WINTTY_TEST_CONFIG
# guard for every child launched while the session is active.
#
# Why both: XDG_CONFIG_HOME alone moves every read and every writer (the
# app appends wintty\config.wintty under the root), and WINTTY_TEST_CONFIG
# turns a harness that forgot the root into a loud startup refusal (the app
# exits before the window with a line naming both paths) instead of a
# silent write into the real %APPDATA% config. The two together are the
# repo's one spelling of "launch the app from a test"; the source-scan test
# (Ghostty.Tests HarnessConfigIsolationScanTests) fails any launch that
# uses neither this helper nor seam-client.ps1's Start-SeamSession.
#
# Dot-source from a harness, keep the session, and pair Enter with Exit in
# a finally when the script must restore mid-flight (multi-root
# orchestration, dot-sourced use). A linear script that launches once and
# exits may skip the Exit: the env vars die with the script process, and
# the 24h sweep below reaps the staged directory.

# 128 bits of hex, so two harnesses racing on one machine cannot share a
# root. RandomNumberGenerator rather than Get-Random, matching
# seam-client's token (whose reasoning applies here too).
function New-WinttyTestConfigRoot {
    $bytes = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return ('wintty-testcfg-' + [System.Convert]::ToHexString($bytes).ToLowerInvariant())
}

function Enter-WinttyTestConfig {
    param(
        # Staged as wintty\config.wintty inside the root. Empty stages
        # nothing: the app creates its own config under the root, which is
        # the honest "unconfigured" baseline.
        [string]$ConfigText = '',
        # Also give every launch a state tree of its own, WINTTY_STATE_BASE
        # at <root>\state, so logs, crash.log, session and window state are
        # the run's: what the coexistence guard asks for before a launch may
        # run beside another Wintty. Opt-in here, where Start-SeamSession
        # makes it the default, because run-win-launch.ps1 shares this
        # helper for a developer's own isolated launch.
        [switch]$PrivateStateBase
    )
    # Opportunistic, bounded: a crashed run leaks its root, and a leak left
    # forever is indistinguishable from a fixture. Anything older than a day
    # belongs to no live run. Wrapped in try/catch because a sweep failure
    # must never take the harness down before it starts.
    try {
        $cutoff = (Get-Date).AddHours(-24)
        Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'wintty-testcfg-*' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -lt $cutoff.ToUniversalTime() } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    } catch { }

    $dir = Join-Path $env:TEMP (New-WinttyTestConfigRoot)
    if ($ConfigText -ne '') {
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'wintty') | Out-Null
        $ConfigText | Set-Content (Join-Path $dir 'wintty\config.wintty') -Encoding utf8
    }

    $session = @{
        Dir    = $dir
        OrigXdg        = if (Test-Path Env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { $null }
        OrigTestConfig = if (Test-Path Env:WINTTY_TEST_CONFIG) { $env:WINTTY_TEST_CONFIG } else { $null }
        OrigStateBase  = if (Test-Path Env:WINTTY_STATE_BASE) { $env:WINTTY_STATE_BASE } else { $null }
        StateBase      = $null
    }
    $env:XDG_CONFIG_HOME = $dir
    $env:WINTTY_TEST_CONFIG = '1'
    if ($PrivateStateBase) {
        $session.StateBase = Join-Path $dir 'state'
        New-Item -ItemType Directory -Force -Path $session.StateBase | Out-Null
        $env:WINTTY_STATE_BASE = $session.StateBase
    }
    return $session
}

# The crash.log files the launches wrote under the session's private state
# tree, as { Path, Text }; empty without one. The tree is searched rather
# than spelled, because the directory under it is named after the edition.
# After Exit-WinttyTestConfig it answers with what Exit read before removing
# the root.
function Get-WinttyTestConfigCrashLogs {
    param([Parameter(Mandatory)]$Session)
    if ($Session.ContainsKey('CrashLogs')) { return @($Session.CrashLogs) }
    if (-not $Session.StateBase) { return @() }
    return @(Get-ChildItem -LiteralPath $Session.StateBase -Recurse -File -Filter crash.log -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 0 } | ForEach-Object {
            $text = try { [System.IO.File]::ReadAllText($_.FullName) } catch { '' }
            [pscustomobject]@{ Path = $_.FullName; Text = $text }
        })
}

function Exit-WinttyTestConfig {
    param([Parameter(Mandatory)]$Session)
    if ($null -ne $Session.OrigXdg) { $env:XDG_CONFIG_HOME = $Session.OrigXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigTestConfig) { $env:WINTTY_TEST_CONFIG = $Session.OrigTestConfig }
    else { Remove-Item Env:WINTTY_TEST_CONFIG -ErrorAction SilentlyContinue }
    if ($Session.StateBase) {
        if ($null -ne $Session.OrigStateBase) { $env:WINTTY_STATE_BASE = $Session.OrigStateBase }
        else { Remove-Item Env:WINTTY_STATE_BASE -ErrorAction SilentlyContinue }
        # Read out before the root goes, so a crash check after Exit still
        # has the evidence.
        if (-not $Session.ContainsKey('CrashLogs')) { $Session.CrashLogs = @(Get-WinttyTestConfigCrashLogs $Session) }
    }
    Remove-Item $Session.Dir -Recurse -Force -ErrorAction SilentlyContinue
}
