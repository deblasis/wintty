# Control: splatting an armed launch of the app must stay clean.
$env:WINTTY_TEST_CONFIG = '1'
$sp = @{ FilePath = 'C:\b1\Wintty.exe' }
Start-Process @sp
