# Control: GetRandomFileName form.
$env:XDG_CONFIG_HOME = Join-Path $env:TEMP ([IO.Path]::GetRandomFileName())
