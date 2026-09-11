# Fixture: a member-shape restore through the WRONG member (R2-3): the
# object saved the value under OrigXdg, the assignment reads OtherXdg.
$Session = @{ OrigXdg = if (Test-Path Env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { $null } }
$env:XDG_CONFIG_HOME = $Session.OtherXdg
