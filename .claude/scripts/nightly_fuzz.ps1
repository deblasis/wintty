# Nightly quality-control run for the windows branch.
#
# Runs the full test ladder and the fuzz suite against a fresh checkout of
# origin/windows in a dedicated worktree, and files a P1 issue on
# deblasis/ghostty for anything that breaks. Intended to run from a scheduled
# task in an idle window (see register_nightly_fuzz.ps1); it derives every
# path from its own location so nothing machine-specific is hardcoded.
#
# The fuzz suite drives the real GUI and needs an unlocked interactive
# desktop. When the workstation is locked the fuzz leg is skipped and the
# skip is recorded; if no fuzz leg has succeeded for 7 days, that starvation
# is itself filed as an issue, so a silent skip cannot masquerade as
# coverage.
#
# Issue filing dedups by title: while an issue for a category is open, new
# failures in that category are logged but not re-filed.

param([switch]$DryRun)

$ErrorActionPreference = 'Continue'
$repo = 'deblasis/ghostty'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
$logDir = Join-Path $repoRoot '.claude\nightly-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$log = Join-Path $logDir "$stamp.log"
Start-Transcript -Path $log | Out-Null

function Get-LogTail {
    if (Test-Path $log) { (Get-Content $log -Tail 60) -join "`n" } else { '' }
}

function File-Issue([string]$title, [string]$detail) {
    $open = gh issue list --repo $repo --state open --search "in:title `"$title`"" --json number --jq 'length' 2>$null
    if ($open -and [int]$open -gt 0) {
        Write-Host "nightly: issue already open for '$title', not re-filing"
        return
    }
    if ($DryRun) { Write-Host "nightly: DRYRUN would file '$title'"; return }
    gh label create P1 --repo $repo --color B60205 --description 'Break found by the nightly quality run' 2>$null
    $bodyFile = Join-Path $logDir "issue-body.tmp.md"
    @"
Found by the nightly quality run on $(Get-Date -Format 'yyyy-MM-dd HH:mm') at commit $script:sha.

$detail

Log tail:
``````
$(Get-LogTail)
``````
Full log: .claude/nightly-logs/$stamp.log on the build machine.
"@ | Set-Content $bodyFile
    gh issue create --repo $repo --title $title --label P1 --body-file $bodyFile
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
}

# Fresh checkout of origin/windows in a dedicated worktree, so nightly runs
# never touch a worktree a session is using.
git -C $repoRoot fetch origin windows
$wt = Join-Path $repoRoot '.claude\worktrees\nightly'
if (-not (Test-Path $wt)) {
    git -C $repoRoot worktree add --detach $wt origin/windows
}
git -C $wt checkout --detach origin/windows
git -C $wt reset --hard origin/windows
git -C $wt clean -fdx -e .zig-cache -e zig-out
$script:sha = (git -C $wt rev-parse --short HEAD).Trim()
Write-Host "nightly: running against origin/windows @ $script:sha"

# Leg 1: full test ladder (headless).
just --justfile (Join-Path $wt 'justfile') --working-directory $wt test
$testRc = $LASTEXITCODE
Write-Host "nightly: zig tests rc=$testRc"
just --justfile (Join-Path $wt 'justfile') --working-directory $wt test-win
$testWinRc = $LASTEXITCODE
Write-Host "nightly: windows tests rc=$testWinRc"

if ($testRc -ne 0) { File-Issue '[nightly] zig test suite failed on windows branch' "``just test`` exited $testRc." }
if ($testWinRc -ne 0) { File-Issue '[nightly] Windows test suite failed on windows branch' "``just test-win`` exited $testWinRc." }

# Leg 2: fuzz suite (needs an unlocked interactive desktop for GUI input).
$locked = [bool](Get-Process LogonUI -ErrorAction SilentlyContinue)
$fuzzStateFile = Join-Path $logDir 'last-fuzz-success.txt'
if ($locked) {
    Write-Host 'nightly: workstation is locked, skipping the GUI fuzz leg (recorded as a skip, not a pass)'
} else {
    just --justfile (Join-Path $wt 'justfile') --working-directory $wt fuzz
    $fuzzRc = $LASTEXITCODE
    Write-Host "nightly: fuzz rc=$fuzzRc"
    switch ($fuzzRc) {
        0 { Get-Date -Format 'yyyy-MM-dd' | Set-Content $fuzzStateFile }
        1 { File-Issue '[nightly] fuzz suite found product failures' "``just fuzz`` exited 1 (product findings)." }
        default { File-Issue '[nightly] fuzz suite could not run' "``just fuzz`` exited $fuzzRc (harness failure, coverage is not running)." }
    }
}

# Starvation check: a skipped fuzz leg must not silently become the norm.
$lastSuccess = if (Test-Path $fuzzStateFile) { [datetime](Get-Content $fuzzStateFile -TotalCount 1) } else { $null }
if (-not $lastSuccess -or ((Get-Date) - $lastSuccess).TotalDays -gt 7) {
    File-Issue '[nightly] fuzz starvation: no successful fuzz run in 7 days' 'The GUI fuzz leg has been skipped or failing for over a week; fuzz coverage is effectively off.'
}

Stop-Transcript | Out-Null
