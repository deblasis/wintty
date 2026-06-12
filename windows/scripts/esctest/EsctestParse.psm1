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

Export-ModuleMember -Function Parse-EsctestLog
