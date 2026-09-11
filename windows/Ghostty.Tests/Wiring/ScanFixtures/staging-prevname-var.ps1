# Fixture: a prev-named variable holding a fixed path is not a restore.
$previousRun = 'C:\wt\rev1084-att\fixed-root'
$env:XDG_CONFIG_HOME = $previousRun
