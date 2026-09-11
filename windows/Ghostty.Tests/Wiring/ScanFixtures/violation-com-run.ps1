# Fixture: a COM shell launching the app.
$shell = New-Object -ComObject WScript.Shell
$shell.Run('C:\wt\Wintty.exe', 1, $false)
