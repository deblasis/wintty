# Control: the same bare launch, armed in-file, stays clean.
export WINTTY_TEST_CONFIG=1
export XDG_CONFIG_HOME="${TMPDIR:-/tmp}/wintty-ok-$$"
Wintty.exe --flag
