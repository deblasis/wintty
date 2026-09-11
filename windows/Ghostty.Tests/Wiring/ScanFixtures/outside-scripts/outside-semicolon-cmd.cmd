@echo off
rem Fixture: bare exe after a semicolon (RED: must be flagged).
verify other 2>nul & echo ok ; C:\b1\Wintty.exe --flag
