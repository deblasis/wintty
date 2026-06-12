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

Export-ModuleMember -Function Parse-EsctestLog, Classify-EsctestResults
