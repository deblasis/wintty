# Fixture: every launch leg carries +crash (clean).
$ExePath = 'C:\b1\Wintty.exe'
Start-Process -FilePath $ExePath -ArgumentList '+crash', 'handled-storm'
Start-Process -FilePath $ExePath `
    -ArgumentList '+crash', 'native-seh' `
    -WorkingDirectory 'C:\b1'
