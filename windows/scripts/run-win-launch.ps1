#requires -Version 7
# The user launcher behind `just run-win` / `just run-win-release`.
#
# Founder rule for run-win: it is how USERS run the OSS build they
# compiled, so it is a user launcher, not a test, and the default for a
# human terminal is the user's real config, unchanged.
#
# Modes, resolved from the environment (set them in the shell that runs
# just; the justfile does not export its own variables):
#   - default (a human terminal): the real config, no ceremony.
#   - ISOLATED_CONFIG=1: explicit opt-in to a fresh random temp XDG root
#     with WINTTY_TEST_CONFIG armed.
#   - an agent session: isolated by default. Claude Code sets CLAUDECODE=1
#     in every process it spawns, and CLAUDECODE is absent from the User
#     and Machine environments, so a human terminal never carries it. The
#     April taint was an agent manual test pass through a dev build,
#     which is exactly the path this closes without affecting users.
#   - REAL_CONFIG=1: overrides every other spelling, runs against the real
#     config, and announces itself loudly, because running an agent session
#     against the real config is a decision, and decisions should be
#     visible.
param(
    # Not [Parameter(Mandatory)]: a mandatory parameter prompts when the
    # script is dot-sourced for mode probing, and the explicit check below
    # gives a real invocation a better message anyway.
    [string]$ExePath = ''
)
$ErrorActionPreference = 'Stop'

# Pure mode resolution, separated from the launch so a harness can
# dot-source this script and probe the decision table without launching
# anything.
function Get-RunWinMode {
    if ($env:REAL_CONFIG -eq '1') { return 'real-forced' }
    if ($env:ISOLATED_CONFIG -eq '1') { return 'isolated-explicit' }
    if ($env:CLAUDECODE -eq '1') { return 'isolated-agent' }
    return 'real'
}

# The confirmation's two effects are injectable so the decision table is
# unit-testable without a console at all: a line reader and an
# is-interactive predicate, both scriptblocks. The defaults are the real
# console behaviors; probes pass fakes.
$script:ReadRealConfigConfirmation = {
    param([string]$Prompt)
    Read-Host $Prompt
}
$script:TestRealConfigConsoleInteractive = {
    -not [Console]::IsInputRedirected -and $Host.UI.IsInteractive
}

# The confirmation primitive: asks for the typed word whenever invoked and
# refuses every non-interactive context. Returns $true only when a human
# at an interactive console typed REAL.
#
# Boundary note (deliberate): the CALLER applies this only when CLAUDECODE
# marks the session as an agent's. CLAUDECODE is self-reported by the same
# process, so this gate is a VISIBILITY layer that makes agent misuse loud,
# not a boundary. The boundary is the app-side WINTTY_TEST_CONFIG guard and
# its env-independent known-folder temp anchor, which no environment
# variable in this process can move.
function Confirm-RealConfigUse {
    param(
        [Parameter(Mandatory)][scriptblock]$ReadInput,
        [Parameter(Mandatory)][scriptblock]$IsInteractive
    )
    if (-not (& $IsInteractive)) {
        Write-Host '  REAL_CONFIG cannot be used from an agent session without a human' -ForegroundColor Red
        Write-Host '  at the keyboard: the console is non-interactive, so the typed' -ForegroundColor Red
        Write-Host '  confirmation cannot be given. Use the isolated default instead' -ForegroundColor Red
        Write-Host '  (this is what an agent session gets without REAL_CONFIG).' -ForegroundColor Red
        return $false
    }
    $answer = & $ReadInput 'Type REAL to run against YOUR REAL config'
    if ($answer -ne 'REAL') {
        Write-Host 'not confirmed; aborting (nothing was launched).' -ForegroundColor Red
        return $false
    }
    return $true
}

# Dot-sourced (InvocationName '.') means someone is probing Get-RunWinMode
# or Confirm-RealConfigUse without launching anything; the launch is for a
# real invocation only.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $ExePath) { throw 'run-win-launch.ps1 needs -ExePath <path to Wintty.exe>' }
$mode = Get-RunWinMode

if ($mode -eq 'real-forced') {
    $root = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME }
            else { "$env:APPDATA (the app appends wintty\ or ghostty\ under it)" }
    Write-Host ''
    Write-Host ('*' * 72) -ForegroundColor Red
    Write-Host '  REAL_CONFIG=1: launching against YOUR REAL config.' -ForegroundColor Red
    Write-Host '  Every read and every write lands in the real per-user config.' -ForegroundColor Red
    Write-Host "  Root: $root" -ForegroundColor Red
    Write-Host ('*' * 72) -ForegroundColor Red
    Write-Host ''

    # From inside an agent session the real config needs a human at the
    # keyboard: the typed confirmation is something an agent cannot give
    # (an agent shell's stdin is redirected or null, so the gate refuses
    # before even asking). A human types it once; a human without
    # CLAUDECODE never sees the prompt at all. Visibility layer, not a
    # boundary: see the note on Confirm-RealConfigUse.
    if ($env:CLAUDECODE -eq '1') {
        if (-not (Confirm-RealConfigUse `
                -ReadInput $script:ReadRealConfigConfirmation `
                -IsInteractive $script:TestRealConfigConsoleInteractive)) {
            exit 3
        }
    }
}

# After the banner, not before: the announcement is the decision, and a
# bad exe path should not take it with it.
$exe = (Resolve-Path -LiteralPath $ExePath).Path
if (-not (Test-Path -LiteralPath $exe)) { throw "missing exe: $ExePath" }

if ($mode -in 'isolated-explicit', 'isolated-agent') {
    . (Join-Path $PSScriptRoot 'lib/test-config.ps1')
    # Enter without Exit on purpose: the GUI process outlives this script,
    # and the app keeps writing into the root after pwsh returns. Exiting
    # here would delete the live config out from under the window; the
    # helper's 24h sweep is what reaps it instead.
    $session = Enter-WinttyTestConfig
    $because = if ($mode -eq 'isolated-agent') { 'agent session, CLAUDECODE=1' }
               else { 'ISOLATED_CONFIG=1' }
    Write-Host ("run-win: isolated config ({0}): {1}" -f $because, $session.Dir)
    Write-Host 'run-win: WINTTY_TEST_CONFIG=1 is armed, so a lost root is a loud refusal.'
    Write-Host 'run-win: to run against the real config instead, pass REAL_CONFIG=1.'
}

# Launch and return. A GUI-subsystem process detaches immediately, which is
# why the justfile can run this outside any lane.
$proc = Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path -Parent $exe)
Write-Host ("run-win: launched pid {0} (mode: {1})" -f $proc.Id, $mode)
