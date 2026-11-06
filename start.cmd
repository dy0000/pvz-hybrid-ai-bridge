@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\launch_sandbox.ps1" %*
if errorlevel 1 pause
