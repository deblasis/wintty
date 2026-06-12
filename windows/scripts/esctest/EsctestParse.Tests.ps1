$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/EsctestParse.psm1" -Force

$script:fails = 0
function Assert-Equal($expected, $actual, $msg) {
    if ($expected -ne $actual) { $script:fails++; Write-Host "FAIL: $msg`n  expected=[$expected] actual=[$actual]" }
    else { Write-Host "ok: $msg" }
}

# --- ConvertFrom-EsctestLog ---
$recs = ConvertFrom-EsctestLog -Path "$PSScriptRoot/fixtures/sample.esctestlog"
Assert-Equal 5 $recs.Count 'parses 5 test records (ignores framing/summary)'

$byName = @{}; foreach ($r in $recs) { $byName[$r.Name] = $r }
Assert-Equal 'PASS'    $byName['CR.test_CR_Basic'].Status              'CR_Basic = PASS'
Assert-Equal 'CR'      $byName['CR.test_CR_Basic'].Section             'section parsed from class'
Assert-Equal 'FAIL'    $byName['CR.test_CR_MovesToLeftMargin'].Status  'CR fail = FAIL'
Assert-Equal 'Mismatch' $byName['CR.test_CR_MovesToLeftMargin'].Reason 'TestFailure traceback = Mismatch reason'
Assert-Equal 'FAIL'    $byName['DECRQSS.test_DECRQSS_SGR'].Status      'DECRQSS fail = FAIL'
Assert-Equal 'DECRQSS' $byName['DECRQSS.test_DECRQSS_SGR'].Section     'DECRQSS section'
Assert-Equal 'Timeout' $byName['DECRQSS.test_DECRQSS_SGR'].Reason      'Timeout-waiting-to-read traceback = Timeout reason'
Assert-Equal 'KNOWN_BUG' $byName['SM.test_SM_IRM'].Status              'fails-as-expected = KNOWN_BUG'
Assert-Equal 'SKIPPED' $byName['DECSET.test_DECSET_AltScreen'].Status  'skipped = SKIPPED'

# --- ConvertTo-EsctestClassification ---
$cls = ConvertTo-EsctestClassification -Records $recs
$cByName = @{}; foreach ($c in $cls) { $cByName[$c.Name] = $c }
# FAIL buckets by reason: response-timeout = ConPTY-timeout; readback-differed = Mismatch-review.
Assert-Equal 'ConPTY-timeout'  $cByName['DECRQSS.test_DECRQSS_SGR'].Bucket      'read-timeout fail = ConPTY-timeout'
Assert-Equal 'Mismatch-review' $cByName['CR.test_CR_MovesToLeftMargin'].Bucket  'readback-differed fail = Mismatch-review'
Assert-Equal 'Pass'            $cByName['CR.test_CR_Basic'].Bucket              'PASS bucket'
Assert-Equal 'Known-bug'       $cByName['SM.test_SM_IRM'].Bucket                'KNOWN_BUG bucket'
Assert-Equal 'Skipped'         $cByName['DECSET.test_DECSET_AltScreen'].Bucket  'SKIPPED bucket'

# --- Format-EsctestReport ---
$md = Format-EsctestReport -Classified $cls -Title 'Smoke'
Assert-Equal $true ($md -match '(?m)^\# VT-compliance baseline: Smoke') 'has title heading'
Assert-Equal $true ($md -match 'Pass:\s*1')            'summary counts passes'
Assert-Equal $true ($md -match 'Mismatch-review:\s*1') 'summary counts mismatch-review'
Assert-Equal $true ($md -match 'ConPTY-timeout:\s*1')  'summary counts conpty-timeout'
Assert-Equal $true ($md -match 'CR\.test_CR_MovesToLeftMargin') 'lists a mismatch-review test'

if ($script:fails -gt 0) { Write-Host "`n$script:fails assertion(s) failed"; exit 1 } else { Write-Host "`nAll assertions passed"; exit 0 }
