param(
    [Parameter(Mandatory)][string]$ExePath
)
$ErrorActionPreference = 'Stop'
$env:WINTTY_TEST_SEAM = '1'
$xdg = Join-Path $env:TEMP ("wintty-seam-probe-" + [guid]::NewGuid().ToString('N'))
$env:XDG_CONFIG_HOME = $xdg
New-Item -ItemType Directory -Force -Path (Join-Path $xdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@ | Set-Content (Join-Path $xdg 'wintty\config.wintty') -Encoding utf8

$exe = $ExePath
$proc = Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path -Parent $exe)
$deadline = [datetime]::UtcNow.AddSeconds(60)
while ([datetime]::UtcNow -lt $deadline) {
    if ($proc.HasExited) { throw "app exited early" }
    if ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\wintty-test-seam') { break }
    Start-Sleep -Milliseconds 200
}
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'wintty-test-seam', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(10000)
$r = [System.IO.StreamReader]::new($pipe)
$w = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false)); $w.AutoFlush = $true

$w.WriteLine('{"op":"get-state"}')
"STATE: " + $r.ReadLine()
$w.WriteLine('{"op":"seed-tabs","count":5,"titles":["tab-1","tab-2","tab-3","tab-4","tab-5"]}')
"SEED RAW: " + $r.ReadLine()
Start-Sleep -Seconds 2
"alive=" + (-not $proc.HasExited)
if ($proc.HasExited) { "exitcode=" + $proc.ExitCode }
try { $w.Dispose(); $r.Dispose(); $pipe.Dispose() } catch {}
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Remove-Item $xdg -Recurse -Force -ErrorAction SilentlyContinue
