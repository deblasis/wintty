# Control: a composed splat launching an armed, isolated app stays clean.
$env:WINTTY_TEST_CONFIG = '1'
$session = Enter-WinttyTestConfig
$common = @{ PassThru = $true }
$extra = @{ FilePath = 'C:\b1\Wintty.exe' }
$sp = $common + $extra
Start-Process @sp
