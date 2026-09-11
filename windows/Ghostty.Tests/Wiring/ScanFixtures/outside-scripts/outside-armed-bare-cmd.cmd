@echo off
rem Control: the same bare launch, armed in-file, stays clean.
set WINTTY_TEST_CONFIG=1
set XDG_CONFIG_HOME=%TEMP%\wintty-ok-%RANDOM%
C:1\Wintty.exe --flag
