# Fixture: arming only inside a conditional blesses nothing.
if ($Real) {
    $env:WINTTY_TEST_CONFIG = '1'
}
Start-Process -FilePath $ExePath
