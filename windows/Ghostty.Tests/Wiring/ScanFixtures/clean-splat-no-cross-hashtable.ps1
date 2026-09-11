# Control: no cross-hashtable read (R4-2). The scanned hashtable has no
# FilePath and closes on its own line; the NEIGHBOUR on the next line
# holds an app path that must NOT become evidence for @h, so this tooling
# launch must stay clean.
$h = @{}
$neighbour = @{ FilePath = 'C:\b1\Wintty.exe' }
Start-Process @h -FilePath 'pwsh.exe'
