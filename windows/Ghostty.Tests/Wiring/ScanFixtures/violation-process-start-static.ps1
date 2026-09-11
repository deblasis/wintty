# Fixture: the static Process.Start form, unguarded.
$ExePath = "C:/b1/Wintty.exe"
[System.Diagnostics.Process]::Start($ExePath) | Out-Null
