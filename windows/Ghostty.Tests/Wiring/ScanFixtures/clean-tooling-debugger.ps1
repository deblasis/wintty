# Fixture: a debugger launch, must NOT be flagged.
$cdb = 'C:/debuggers/cdb.exe'
Start-Process -FilePath $cdb -ArgumentList "-g -G" | Out-Null
