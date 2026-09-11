# Control: the same bare run, armed in-file, stays clean.
export WINTTY_TEST_CONFIG = "1"
export XDG_CONFIG_HOME = $env.TEMP + "/wintty-ok-" + (random)
Wintty.exe --flag
