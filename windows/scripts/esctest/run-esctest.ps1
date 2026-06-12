#requires -Version 7
[CmdletBinding()] param(
    [Parameter(Mandatory)][string]$WinttyExe,       # path to built Wintty.exe
    [string]$Distro = 'Ubuntu-24.04',
    [string]$EsctestDir = '~/esctest2/esctest',     # path inside the distro
    [string]$OutDir = "$env:TEMP\esctest-run",       # Windows-side output dir
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/EsctestParse.psm1" -Force

New-Item -ItemType Directory -Force $OutDir | Out-Null
$logWin  = Join-Path $OutDir 'esctest.log'
$doneWin = Join-Path $OutDir 'esctest.done'
Remove-Item $doneWin, $logWin, (Join-Path $OutDir 'esctest.rc') -ErrorAction SilentlyContinue

# Translate the Windows OutDir to the distro's /mnt path (for esctest --logfile
# and the markers, all written distro-side onto the DrvFs mount).
$drive   = $OutDir.Substring(0,1).ToLower()
$mnt     = "/mnt/$drive" + ($OutDir.Substring(2) -replace '\\','/')
$logMnt  = "$mnt/esctest.log"
$rcMnt   = "$mnt/esctest.rc"
$doneMnt = "$mnt/esctest.done"

# Put the run logic in a bash script (invoked as `bash -l <script>`) rather than
# embedding it in the ghostty `command` value: this avoids nested-quote / `;` /
# `$?` quoting through ghostty's config parser entirely. The script MUST use LF
# line endings (CRLF makes bash choke on `\r`), so write raw bytes, not
# Set-Content (which would emit CRLF + BOM).
$scriptWin = Join-Path $OutDir 'run.sh'
$scriptMnt = "$mnt/run.sh"
$bash = @(
    '#!/usr/bin/env bash'
    "cd $EsctestDir || exit 3"
    "python3 esctest.py --expected-terminal=xterm --max-vt-level=5 --timeout=1 --logfile='$logMnt'"
    "echo `$? > '$rcMnt'"
    "touch '$doneMnt'"
) -join "`n"
[System.IO.File]::WriteAllText($scriptWin, $bash + "`n", (New-Object System.Text.UTF8Encoding($false)))

# Launch Wintty via a temp ghostty config (the WinUI app ignores --command;
# XDG_CONFIG_HOME + command= is the proven harness, see #494 OSC7 work). The
# command splits cleanly on spaces into wsl.exe argv; the script path is
# space-free (under $env:TEMP).
$cfgDir = Join-Path $OutDir 'cfg'
# ghostty reads $XDG_CONFIG_HOME/ghostty/config (the `ghostty/` subdir is
# required; without it the surface falls back to the default shell).
$cfgGhostty = Join-Path $cfgDir 'ghostty'
New-Item -ItemType Directory -Force $cfgGhostty | Out-Null
"command = wsl.exe -d $Distro -- bash -l $scriptMnt" |
    Set-Content -LiteralPath (Join-Path $cfgGhostty 'config') -Encoding utf8

# Inherit the full current environment (so Wintty finds its system deps) and add
# only the XDG_CONFIG_HOME override, rather than -Environment (merge-vs-replace
# semantics vary). Restore afterward.
$prevXdg = $env:XDG_CONFIG_HOME
$proc = $null
try {
    $env:XDG_CONFIG_HOME = $cfgDir
    $proc = Start-Process -FilePath $WinttyExe -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path $doneWin) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
}
finally {
    $env:XDG_CONFIG_HOME = $prevXdg
    # Close ONLY the Wintty we launched (never a blind kill).
    if ($proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {} }
}
$complete = Test-Path $doneWin

if (-not (Test-Path $logWin)) {
    throw "No esctest log at $logWin -- the run never started. Check that the Wintty surface spawned wsl.exe (temp config at $cfgDir/config) and that $scriptMnt is reachable in the distro."
}
if (-not $complete) { Write-Warning "esctest did not finish within ${TimeoutSec}s; parsing the partial log." }

# @() forces an array so an empty/partial log yields an empty collection, not
# $null (which would fail the downstream Mandatory binding).
$recs = @(ConvertFrom-EsctestLog -Path $logWin)
if ($recs.Count -eq 0) { Write-Warning "No test records parsed from $logWin (esctest may have crashed before running tests)." }
$cls  = ConvertTo-EsctestClassification -Records $recs
$title = "Wintty/ConPTY via WSL $Distro" + ($(if (-not $complete) { ' (INCOMPLETE)' } else { '' }))
$report = Format-EsctestReport -Classified $cls -Title $title
$reportPath = Join-Path $OutDir 'esctest-baseline.md'
$report | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Host "Parsed $($recs.Count) tests. Report: $reportPath"
$cls | Group-Object Bucket | Sort-Object Name | ForEach-Object { Write-Host ("  {0}: {1}" -f $_.Name, $_.Count) }
