# Control: guid form.
$env:XDG_CONFIG_HOME = Join-Path $env:TEMP ("wintty-ok-" + [guid]::NewGuid().ToString("N"))
