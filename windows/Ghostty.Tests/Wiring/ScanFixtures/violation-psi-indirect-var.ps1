# Fixture: ProcessStartInfo with the app set by property, launched via ::Start.
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $ExePath
[System.Diagnostics.Process]::Start($psi) | Out-Null
