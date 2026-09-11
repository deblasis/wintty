# Fixture: the += composition of a splat hashtable (R3-1).
$sp = @{ PassThru = $true }
$sp += @{ FilePath = 'C:\wt\rev1084-att\Wintty.exe' }
Start-Process @sp
