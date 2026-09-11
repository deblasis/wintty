# Fixture: cdb attaching to a running pid, not launching anything.
$cdb = 'C:/dbg/cdb.exe'
Start-Process -FilePath $cdb -ArgumentList '-g', '-p', '4242'
