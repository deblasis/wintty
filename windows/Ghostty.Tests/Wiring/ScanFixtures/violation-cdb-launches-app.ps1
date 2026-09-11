# Fixture: cdb starting the app as its debuggee.
$cdb = 'C:/dbg/cdb.exe'
Start-Process -FilePath $cdb -ArgumentList '-g', 'C:/bin/Wintty.exe'
