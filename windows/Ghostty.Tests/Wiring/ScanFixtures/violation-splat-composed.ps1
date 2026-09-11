# Fixture: the merged-splat shape, verbatim from re-review 3 (R3-1).
$common = @{ PassThru = $true }
$extra = @{ FilePath = 'C:\wt\rev1084-att\Wintty.exe' }
$sp = $common + $extra
Start-Process @sp
