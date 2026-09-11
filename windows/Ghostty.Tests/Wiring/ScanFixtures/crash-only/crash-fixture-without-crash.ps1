# Fixture: one leg is missing +crash (flagged): that leg would run fully
# unguarded while the file rides the crash-only allowlist.
$ExePath = 'C:\b1\Wintty.exe'
Start-Process -FilePath $ExePath -ArgumentList '+crash', 'handled-storm'
Start-Process -FilePath $ExePath -WorkingDirectory 'C:\b1'
