$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/EsctestParse.psm1" -Force

$script:fails = 0
function Assert-Equal($expected, $actual, $msg) {
    if ($expected -ne $actual) { $script:fails++; Write-Host "FAIL: $msg`n  expected=[$expected] actual=[$actual]" }
    else { Write-Host "ok: $msg" }
}

# --- Parse-EsctestLog ---
$recs = Parse-EsctestLog -Path "$PSScriptRoot/fixtures/sample.log"
Assert-Equal 5 $recs.Count 'parses 5 test records (ignores framing/summary)'

$byName = @{}; foreach ($r in $recs) { $byName[$r.Name] = $r }
Assert-Equal 'PASS'    $byName['CR.test_CR_Basic'].Status              'CR_Basic = PASS'
Assert-Equal 'CR'      $byName['CR.test_CR_Basic'].Section             'section parsed from class'
Assert-Equal 'FAIL'    $byName['CR.test_CR_MovesToLeftMargin'].Status  'CR fail = FAIL'
Assert-Equal 'FAIL'    $byName['DECRQSS.test_DECRQSS_SGR'].Status      'DECRQSS fail = FAIL'
Assert-Equal 'DECRQSS' $byName['DECRQSS.test_DECRQSS_SGR'].Section     'DECRQSS section'
Assert-Equal 'KNOWN_BUG' $byName['SM.test_SM_IRM'].Status              'fails-as-expected = KNOWN_BUG'
Assert-Equal 'SKIPPED' $byName['DECSET.test_DECSET_AltScreen'].Status  'skipped = SKIPPED'

if ($script:fails -gt 0) { Write-Host "`n$script:fails assertion(s) failed"; exit 1 } else { Write-Host "`nAll assertions passed"; exit 0 }
