@echo off
rem Fixture: bare exe after || (RED: must be flagged).
robocopy nul nul || C:\b1\Wintty.exe --flag
