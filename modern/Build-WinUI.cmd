@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\BuildWinUI.ps1" -Action Build %*
if errorlevel 1 pause