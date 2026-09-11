# Fixture: a fixed root set as a param default (R2-3), verbatim shape.
param([string]$XdgRoot = 'C:\fixed')
$env:XDG_CONFIG_HOME = $XdgRoot
