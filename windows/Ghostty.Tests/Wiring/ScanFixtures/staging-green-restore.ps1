# Control: a genuine save and restore, paired by the variable.
$savedXdg = $env:XDG_CONFIG_HOME
$env:XDG_CONFIG_HOME = Join-Path $env:TEMP ("wintty-ok-" + [guid]::NewGuid().ToString("N"))
$env:XDG_CONFIG_HOME = $savedXdg
