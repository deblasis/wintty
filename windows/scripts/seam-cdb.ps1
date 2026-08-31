#requires -Version 7
<#
    Drives the gap scenario while the app runs under cdb, and takes a FULL
    dump (.dump /ma) at the fail-fast. WER LocalDumps only ever produces
    minidumps here, and a minidump cannot read the stowed exception's stack
    (the pages are simply not in the file) -- the full dump is what names
    the styled container.

    Two sxe arms, because the fail-fast surfaces as either 0xC000027B or
    its escalation 0xC0000602 depending on where the debugger first sees
    it. The dump takes tens of seconds to minutes for a 400MB-commit
    process; the script polls for the file and does not give up early.
    sxe -c has been observed to not fire for this exception on some runs;
    if the dump never appears, the fallback is WER LocalDumps
    (seam-crash-dump.ps1) plus manual analysis of what the minidump holds.

    Exits 0 with the dump path printed on success.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$DumpPath = (Join-Path $env:TEMP 'wintty-seam-crash.dmp')
)
$ErrorActionPreference = 'Stop'
$exe = $ExePath
$cdb = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe'
$log = Join-Path $env:TEMP 'wintty-seam-cdb.log'
$cmds = Join-Path $env:TEMP 'wintty-seam-cdb-cmds.txt'
$xdg = Join-Path $env:TEMP ("wintty-seam-cdb-" + [guid]::NewGuid().ToString('N'))
$env:XDG_CONFIG_HOME = $xdg
$env:WINTTY_TEST_SEAM = '1'
New-Item -ItemType Directory -Force -Path (Join-Path $xdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@ | Set-Content (Join-Path $xdg 'wintty\config.wintty') -Encoding utf8

$dumpArg = ($DumpPath -replace '\\', '\\')
@'
sxe -c ".dump /ma DUMPPATH; q" 0xC000027B
sxe -c ".dump /ma DUMPPATH; q" 0xC0000602
g
'@ -replace 'DUMPPATH', $dumpArg | Set-Content $cmds -Encoding ascii

Remove-Item $log, $DumpPath -ErrorAction SilentlyContinue
$argLine = '-logo "' + $log + '" -c "$<' + $cmds + '" "' + $exe + '"'
$debugger = Start-Process -FilePath $cdb -ArgumentList $argLine -PassThru
$deadline = [datetime]::UtcNow.AddSeconds(90)
while ([datetime]::UtcNow -lt $deadline) {
    if ($debugger.HasExited) { throw "HARNESS: cdb exited before the pipe appeared" }
    if ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\wintty-test-seam') {
        break
    }
    Start-Sleep -Milliseconds 200
}
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.', 'wintty-test-seam', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(10000)
$reader = [System.IO.StreamReader]::new($pipe)
$writer = [System.IO.StreamWriter]::new(
    $pipe, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$writer.NewLine = "`n"

$seed = '{"op":"seed-tabs","count":5,"titles":["tab-1","tab-2","tab-3","tab-4","tab-5"]}'
$pin = '{"op":"pin","index":1}'
$group = '{"op":"group","indices":[2,3]}'
$coll = '{"op":"collapse","index":2,"collapsed":true}'
$ops = @($seed, $pin, $group, $coll)
$crashed = $false
foreach ($op in $ops) {
    Start-Sleep -Milliseconds 400
    try {
        $writer.WriteLine($op)
        [void]$reader.ReadLine()
    }
    catch [System.IO.IOException] {
        $crashed = $true
        break
    }
}

# The dump write can take minutes; poll, do not assume.
$dumpBy = [datetime]::UtcNow.AddSeconds(420)
while ([datetime]::UtcNow -lt $dumpBy) {
    if (Test-Path $DumpPath) {
        $size = (Get-Item $DumpPath).Length
        if ($size -gt 10MB) {
            Start-Sleep -Seconds 5   # let the writer finish
            break
        }
    }
    Start-Sleep -Seconds 5
}
try { $writer.Dispose(); $reader.Dispose(); $pipe.Dispose() } catch {}
if (-not $debugger.WaitForExit(60000)) {
    "cdb still alive after the dump window; killing"
    Stop-Process -Id $debugger.Id -Force -ErrorAction SilentlyContinue
}
if (Test-Path $DumpPath) {
    Write-Host ("DUMP: {0} ({1:N0} bytes)" -f $DumpPath, (Get-Item $DumpPath).Length)
    exit 0
}
Write-Host 'HARNESS: no dump produced; check the cdb log:' (Test-Path $log)
exit 1
