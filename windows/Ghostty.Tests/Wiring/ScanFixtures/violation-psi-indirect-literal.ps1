# Fixture: ProcessStartInfo with a literal app path set by property.
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'C:/bin/Wintty.exe'
[System.Diagnostics.Process]::Start($psi) | Out-Null
