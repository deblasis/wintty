Set-StrictMode -Version Latest

function Parse-EsctestLog {
    [CmdletBinding()] param([Parameter(Mandatory)][string]$Path)
    $records = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -like 'Run test: *') {
            $current = $line.Substring('Run test: '.Length).Trim()
            continue
        }
        if (-not $current) { continue }
        $status = switch -Wildcard ($line) {
            'Passed.'                                              { 'PASS' }
            '*** TEST * FAILED:*'                                 { 'FAIL' }
            'Fails as expected: *'                                { 'KNOWN_BUG' }
            'Skipped because terminal lacks requisite*'           { 'SKIPPED' }
            default                                               { $null }
        }
        if ($status) {
            $section = ($current -split '\.', 2)[0]
            $records.Add([pscustomobject]@{ Name = $current; Section = $section; Status = $status; Detail = $line.Trim() })
            $current = $null
        }
    }
    return $records.ToArray()
}

# DCS/string-family sections ConPTY is known to strip/mangle (see #474 findings).
$script:ConPtyLimitSections = @('DCS','DECRQSS','APC','SOS','PM')

function Classify-EsctestResults {
    [CmdletBinding()] param([Parameter(Mandatory)][object[]]$Records)
    foreach ($r in $Records) {
        $bucket = switch ($r.Status) {
            'PASS'      { 'Pass' }
            'KNOWN_BUG' { 'Known-bug' }
            'SKIPPED'   { 'Skipped' }
            'FAIL'      {
                if ($script:ConPtyLimitSections -contains $r.Section) { 'ConPTY-limit' }
                else { 'Candidate-bug' }
            }
            default     { 'Unknown' }
        }
        [pscustomobject]@{ Name = $r.Name; Section = $r.Section; Status = $r.Status; Bucket = $bucket; Detail = $r.Detail }
    }
}

function Format-EsctestReport {
    [CmdletBinding()] param(
        [Parameter(Mandatory)][object[]]$Classified,
        [Parameter(Mandatory)][string]$Title
    )
    $buckets = 'Pass','Known-bug','Skipped','ConPTY-limit','Candidate-bug','Unknown'
    $counts = @{}; foreach ($b in $buckets) { $counts[$b] = 0 }
    foreach ($c in $Classified) { $counts[$c.Bucket]++ }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# VT-compliance baseline: $Title")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## Summary')
    foreach ($b in $buckets) { [void]$sb.AppendLine("- ${b}: $($counts[$b])") }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## Candidate-Ghostty-bugs (review before filing)')
    foreach ($c in ($Classified | Where-Object Bucket -eq 'Candidate-bug' | Sort-Object Name)) {
        [void]$sb.AppendLine("- ``$($c.Name)`` -- $($c.Detail)")
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## ConPTY-limit failures (expected, DCS/string-family)')
    foreach ($c in ($Classified | Where-Object Bucket -eq 'ConPTY-limit' | Sort-Object Name)) {
        [void]$sb.AppendLine("- ``$($c.Name)``")
    }
    return $sb.ToString()
}

Export-ModuleMember -Function Parse-EsctestLog, Classify-EsctestResults, Format-EsctestReport
