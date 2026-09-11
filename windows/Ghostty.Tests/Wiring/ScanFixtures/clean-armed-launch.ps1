# Fixture: the blessed shape, must NOT be flagged.
$env:WINTTY_TEST_CONFIG = '1'
$ExePath = "C:/b1/Wintty.exe"
Start-Process -FilePath $ExePath
