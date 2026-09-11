# Fixture: the reviewer's mutation - +crash only in a trailing comment
# (flagged: it is not an argument of the launch).
$ExePath = 'C:\b1\Wintty.exe'
Start-Process -FilePath $ExePath -ArgumentList '+list-themes' # keep +crash pinned here
