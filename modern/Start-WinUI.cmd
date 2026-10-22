@echo off
chcp 65001 >nul
set DOTNET_CLI_UI_LANGUAGE=zh-CN
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\BuildWinUI.ps1" -Action Run
if errorlevel 1 pause
