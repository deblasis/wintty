# Runs one scenario in a fresh app instance: steps are seam ops; reports
# SURVIVED or CRASHED(op) so the crash's minimal compound can be found.
param(
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$ExePath,
    [string[]]$Ops,
    [int]$GapMs = 0
)
$ErrorActionPreference = 'Stop'

$exe = $ExePath
$xdg = Join-Path $env:TEMP ("wintty-seam-bisect-" + [guid]::NewGuid().ToString('N'))
$env:XDG_CONFIG_HOME = $xdg
$env:WINTTY_TEST_SEAM = '1'
New-Item -ItemType Directory -Force -Path (Join-Path $xdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@ | Set-Content (Join-Path $xdg 'wintty\config.wintty') -Encoding utf8

# Wait out the previous scenario's instance: its pipe server may outlive
# the client by a beat, and connecting to a dying process looks like a
# broken pipe, not a crash.
$goneBy = [datetime]::UtcNow.AddSeconds(15)
while ([datetime]::UtcNow -lt $goneBy) {
    if (-not ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\wintty-test-seam')) { break }
    Start-Sleep -Milliseconds 200
}

$proc = Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path -Parent $exe)
$deadline = [datetime]::UtcNow.AddSeconds(60)
while ([datetime]::UtcNow -lt $deadline) {
    if ($proc.HasExited) { throw "app exited before the pipe appeared" }
    if ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\wintty-test-seam') { break }
    Start-Sleep -Milliseconds 200
}
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'wintty-test-seam', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(10000)
$r = [System.IO.StreamReader]::new($pipe)
$w = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false)); $w.AutoFlush = $true
$w.NewLine = "`n"

$outcome = 'SURVIVED'
try {
    foreach ($op in $Ops) {
        Start-Sleep -Milliseconds $GapMs
        if ($proc.HasExited) { $outcome = "CRASHED(before $op)"; break }
        try {
            $w.WriteLine($op)
            $line = $r.ReadLine()
        }
        catch [System.IO.IOException] {
            $outcome = if ($proc.HasExited) { "CRASHED(during $op)" } else { "PIPE-BROKEN($op)" }
            break
        }
        if ($null -eq $line) {
            $outcome = if ($proc.HasExited) { "CRASHED(during $op)" } else { "NO-REPLY($op)" }
            break
        }
        $resp = $line | ConvertFrom-Json
        if (-not $resp.ok) { $outcome = "REFUSED($op): $($resp.error)"; break }
    }
    Start-Sleep -Milliseconds 800
    if ($outcome -eq 'SURVIVED' -and $proc.HasExited) {
        $outcome = "CRASHED(after last op, code $($proc.ExitCode))"
    }
}
catch {
    $outcome = "HARNESS($($_.Exception.Message))"
}
finally {
    try { $w.Dispose(); $r.Dispose(); $pipe.Dispose() } catch {}
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $xdg -Recurse -Force -ErrorAction SilentlyContinue
}

"$scenario => $outcome"
