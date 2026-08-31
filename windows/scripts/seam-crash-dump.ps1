#requires -Version 7
<#
    Captures a FULL dump of the seam's churn crash (0xC000027B fail-fast,
    XAML 800F1000 E_NER_INVALID_OPERATION underneath) through WER
    LocalDumps, because cdb's sxe -c never fires for a non-continuable
    fail-fast. The registry write is per-user, snapshot before, verified
    after, restored in the finally: the harness holds the state it touches
    and puts it back.

    The scenario is the pacing discriminator's losing compound: 400ms
    between commands, seed 5 tabs, pin, group, collapse. WER does not run
    while a debugger is attached, so nothing else may hold the process.

    Exits 0 with $DumpPath printed on success, 1 when the harness could
    not run, 2 when the app survived (no crash to dump).
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$DumpFolder = (Join-Path $env:TEMP 'wintty-seam-dumps')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')

Assert-NoWintty -Context 'The seam crash-dump capture'
$stamp = Get-WinttyLaunchStamp

# ---- WER LocalDumps, snapshot / set / verify / restore -------------------

$werKey = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$exeKey = "$werKey\Wintty.exe"
$snapshot = $null
if (Test-Path $exeKey) {
    $snapshot = Get-ItemProperty $exeKey
}
function Restore-Wer {
    if ($snapshot) {
        foreach ($prop in $snapshot.PSObject.Properties) {
            if ($prop.Name -in @('PSPath', 'PSParentPath', 'PSChildName',
                                 'PSDrive', 'PSProvider')) { continue }
            Set-ItemProperty $exeKey -Name $prop.Name -Value $prop.Value
        }
    } elseif (Test-Path $exeKey) {
        Remove-Item $exeKey -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $DumpFolder | Out-Null
New-Item -ItemType Directory -Force -Path $exeKey | Out-Null
New-ItemProperty $exeKey -Name DumpFolder -Value $DumpFolder `
    -PropertyType ExpandString -Force | Out-Null
New-ItemProperty $exeKey -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null
New-ItemProperty $exeKey -Name DumpCount -Value 10 -PropertyType DWord -Force | Out-Null

# Read-back verification: the guard's rule -- a setting the harness set
# but cannot read back is a setting it does not have.
$check = Get-ItemProperty $exeKey
if ($check.DumpType -ne 2 -or $check.DumpFolder -ne $DumpFolder) {
    throw 'HARNESS: WER LocalDumps did not read back the values just set'
}

$xdg = Join-Path $env:TEMP ("wintty-seam-dump-" + [guid]::NewGuid().ToString('N'))
$env:XDG_CONFIG_HOME = $xdg
$env:WINTTY_TEST_SEAM = '1'
New-Item -ItemType Directory -Force -Path (Join-Path $xdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@ | Set-Content (Join-Path $xdg 'wintty\config.wintty') -Encoding utf8

$dumpPath = $null
$proc = $null
try {
    $proc = Start-Process -FilePath $ExePath -PassThru `
        -WorkingDirectory (Split-Path -Parent (Resolve-Path $ExePath))
    $deadline = [datetime]::UtcNow.AddSeconds(90)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($proc.HasExited) { throw "HARNESS: app exited before the seam pipe" }
        if ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\wintty-test-seam') {
            break
        }
        Start-Sleep -Milliseconds 200
    }
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', 'wintty-test-seam', [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(20000)
    $reader = [System.IO.StreamReader]::new($pipe)
    $writer = [System.IO.StreamWriter]::new(
        $pipe, [System.Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $writer.NewLine = "`n"

    $ops = @(
        '{"op":"seed-tabs","count":5,"titles":["tab-1","tab-2","tab-3","tab-4","tab-5"]}',
        '{"op":"pin","index":1}',
        '{"op":"group","indices":[2,3]}',
        '{"op":"collapse","index":2,"collapsed":true}',
        '{"op":"toggle-layout"}',
        '{"op":"toggle-layout"}',
        '{"op":"collapse","index":2,"collapsed":false}',
        '{"op":"toggle-layout"}',
        '{"op":"toggle-layout"}'
    )
    $crashed = $false
    foreach ($op in $ops) {
        Start-Sleep -Milliseconds 400
        if ($proc.HasExited) { $crashed = $true; break }
        try {
            $writer.WriteLine($op)
            [void]$reader.ReadLine()
        }
        catch [System.IO.IOException] {
            $crashed = $true
            break
        }
    }
    if (-not $crashed) {
        Write-Host 'PRODUCT-SURVIVED: the scenario did not crash this run'
        exit 2
    }

    # WER owns the death now: wait for the dump to be written.
    $dumpDeadline = [datetime]::UtcNow.AddSeconds(120)
    while ([datetime]::UtcNow -lt $dumpDeadline) {
        $dumpPath = Get-ChildItem $DumpFolder -Filter 'Wintty.exe.*.dmp' `
            -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($dumpPath -and $dumpPath.Length -gt 0) {
            Start-Sleep -Seconds 3   # let WER finish the write
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $dumpPath) {
        throw 'HARNESS: the process died but WER produced no dump'
    }
    Write-Host ("DUMP: {0} ({1:N0} bytes)" -f $dumpPath.FullName, $dumpPath.Length)
    exit 0
}
catch {
    Write-Host $_.Exception.Message
    exit 1
}
finally {
    Restore-Wer
    if ($proc -and -not $proc.HasExited) {
        Stop-WinttyStartedAfter -Since $stamp -ExePath (Resolve-Path $ExePath).Path `
            -ErrorAction SilentlyContinue
    }
    Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue
    Remove-Item Env:WINTTY_TEST_SEAM -ErrorAction SilentlyContinue
    Remove-Item $xdg -Recurse -Force -ErrorAction SilentlyContinue
}
