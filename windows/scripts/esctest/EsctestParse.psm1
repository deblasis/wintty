Set-StrictMode -Version Latest

# Parse an esctest2 logfile into per-test records. esctest logs, per test:
#   Run test: <Class>.<method>
#   <Passed. | *** TEST <name> FAILED: (+traceback) | Fails as expected: .. | Skipped because ..>
# For a FAILED test the following traceback is sniffed for the failure reason:
#   "Timeout waiting to read"  -> Reason=Timeout   (response never returned in time)
#   esctypes.TestFailure       -> Reason=Mismatch  (readback returned but differed)
#   otherwise                  -> Reason=Other
# Framing lines (Reading X window info, tracebacks, the summary) are ignored.
function ConvertFrom-EsctestLog {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $records = [System.Collections.Generic.List[object]]::new()
    $current = $null
    $pendingFail = $null
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -like 'Run test: *') {
            $current = $line.Substring('Run test: '.Length).Trim()
            $pendingFail = $null
            continue
        }
        # While inside a failure's traceback, sniff the exception to set Reason.
        if ($pendingFail) {
            if ($line -match 'Timeout waiting to read') { $pendingFail.Reason = 'Timeout' }
            elseif ($line -match '^esctypes\.TestFailure') { $pendingFail.Reason = 'Mismatch' }
            if ($line.Trim().Length -eq 0) { $pendingFail = $null }   # blank line ends the traceback
            continue
        }
        if (-not $current) { continue }
        $section = ($current -split '\.', 2)[0]
        switch -Wildcard ($line) {
            'Passed.' {
                $records.Add([pscustomobject]@{ Name = $current; Section = $section; Status = 'PASS'; Reason = ''; Detail = $line.Trim() })
                $current = $null
            }
            '*** TEST * FAILED:*' {
                $rec = [pscustomobject]@{ Name = $current; Section = $section; Status = 'FAIL'; Reason = 'Other'; Detail = $line.Trim() }
                $records.Add($rec)
                $pendingFail = $rec
                $current = $null
            }
            'Fails as expected: *' {
                $records.Add([pscustomobject]@{ Name = $current; Section = $section; Status = 'KNOWN_BUG'; Reason = ''; Detail = $line.Trim() })
                $current = $null
            }
            'Skipped because terminal lacks requisite*' {
                $records.Add([pscustomobject]@{ Name = $current; Section = $section; Status = 'SKIPPED'; Reason = ''; Detail = $line.Trim() })
                $current = $null
            }
        }
    }
    return $records.ToArray()
}

# Bucket each record. FAIL splits by Reason: a response-read Timeout is a
# transport/ConPTY limit; a Mismatch (readback returned but differed) needs
# review (could be ConPTY-mangle, an xterm-specific expectation Ghostty does not
# meet, or a genuine Ghostty bug).
function ConvertTo-EsctestClassification {
    [CmdletBinding()] param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Records)
    foreach ($r in $Records) {
        $bucket = switch ($r.Status) {
            'PASS'      { 'Pass' }
            'KNOWN_BUG' { 'Known-bug' }
            'SKIPPED'   { 'Skipped' }
            'FAIL'      {
                switch ($r.Reason) {
                    'Timeout'  { 'ConPTY-timeout' }
                    'Mismatch' { 'Mismatch-review' }
                    default    { 'Fail-other' }
                }
            }
            default     { 'Unknown' }
        }
        [pscustomobject]@{ Name = $r.Name; Section = $r.Section; Status = $r.Status; Reason = $r.Reason; Bucket = $bucket; Detail = $r.Detail }
    }
}

function Format-EsctestReport {
    [CmdletBinding()] param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Classified,
        [Parameter(Mandatory)][string]$Title
    )
    $buckets = 'Pass','Known-bug','Skipped','ConPTY-timeout','Mismatch-review','Fail-other','Unknown'
    $counts = @{}; foreach ($b in $buckets) { $counts[$b] = 0 }
    foreach ($c in $Classified) { $counts[$c.Bucket]++ }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# VT-compliance baseline: $Title")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## Summary')
    foreach ($b in $buckets) { [void]$sb.AppendLine("- ${b}: $($counts[$b])") }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## Mismatch-review (readback returned but differed)')
    [void]$sb.AppendLine('Each is one of: ConPTY-mangled response, an xterm-specific expectation Ghostty does not meet, or a genuine Ghostty bug. Review before filing.')
    [void]$sb.AppendLine('')
    foreach ($c in ($Classified | Where-Object Bucket -eq 'Mismatch-review' | Sort-Object Name)) {
        [void]$sb.AppendLine("- ``$($c.Name)``")
    }
    return $sb.ToString()
}

Export-ModuleMember -Function ConvertFrom-EsctestLog, ConvertTo-EsctestClassification, Format-EsctestReport
