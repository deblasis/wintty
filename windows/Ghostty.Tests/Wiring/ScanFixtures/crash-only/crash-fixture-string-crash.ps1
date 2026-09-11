# Fixture: the reviewer's mutation - +crash inside a string of another
# command on the same line (flagged: not an argument of the launch).
$ExePath = 'C:\b1\Wintty.exe'
Write-Host "docs: +crash exits before the guard" ; Start-Process -FilePath $ExePath -ArgumentList '+list-themes'
