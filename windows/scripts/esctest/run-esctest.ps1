#requires -Version 7
[CmdletBinding()] param(
    [Parameter(Mandatory)][string]$WinttyExe,       # path to built Wintty.exe
    [string]$Distro = 'Ubuntu-24.04',
    [string]$EsctestDir = '~/esctest2/esctest',     # path inside the distro
    [string]$OutDir = "$env:TEMP\esctest-run",       # Windows-side output dir
    [int]$TimeoutSec = 600
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/EsctestParse.psm1" -Force

New-Item -ItemType Directory -Force $OutDir | Out-Null
$logWin = Join-Path $OutDir 'esctest.log'
# Translate the Windows OutDir to the distro's /mnt path for esctest's --logfile.
$drive = $OutDir.Substring(0,1).ToLower()
$mnt = "/mnt/$drive" + ($OutDir.Substring(2) -replace '\\','/')
$logMnt  = "$mnt/esctest.log"
$doneMnt = "$mnt/esctest.done"
$doneWin = Join-Path $OutDir 'esctest.done'
Remove-Item $doneWin,$logWin -ErrorAction SilentlyContinue

# esctest writes its OWN logfile to /mnt/c, then drops a done-marker.
$wslCmd = "cd $EsctestDir && python3 esctest.py --expected-terminal=xterm --max-vt-level=5 --timeout=1 --logfile='$logMnt'; echo `$? > '$mnt/esctest.rc'; touch '$doneMnt'"

# Launch Wintty via a temp ghostty config (the WinUI app ignores --command;
# XDG_CONFIG_HOME + command= is the proven harness, see #494 OSC7 work).
$cfgDir = Join-Path $OutDir 'cfg'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$cfgFile = Join-Path $cfgDir 'config'
"command = wsl.exe -d $Distro -- bash -lc `"$wslCmd`"" | Set-Content -LiteralPath $cfgFile -Encoding utf8
$proc = Start-Process -FilePath $WinttyExe -PassThru -Environment @{ XDG_CONFIG_HOME = $cfgDir }

# Poll for the done-marker.
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while (-not (Test-Path $doneWin) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
$complete = Test-Path $doneWin

# Close ONLY the Wintty we launched (never a blind kill).
try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}

if (-not (Test-Path $logWin)) { throw "No esctest log produced at $logWin (run did not start)" }
if (-not $complete) { Write-Warning "esctest did not finish within ${TimeoutSec}s; parsing partial log." }

$recs = Parse-EsctestLog -Path $logWin
$cls  = ConvertTo-EsctestClassification -Records $recs
$report = Format-EsctestReport -Classified $cls -Title ("Wintty/ConPTY via WSL $Distro" + ($(if(-not $complete){' (INCOMPLETE)'}else{''})))
$reportPath = Join-Path $OutDir 'esctest-baseline.md'
$report | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Parsed $($recs.Count) tests. Report: $reportPath"
$cls | Group-Object Bucket | Sort-Object Name | ForEach-Object { Write-Host ("  {0}: {1}" -f $_.Name, $_.Count) }
