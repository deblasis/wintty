# Fixture: the same psi form launching tooling, must NOT be flagged.
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'pwsh'
[System.Diagnostics.Process]::Start($psi) | Out-Null
