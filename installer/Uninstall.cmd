@echo off
rem Uninstall.cmd - removes RTC Call Monitor (asks for administrator rights)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Uninstall.ps1" %*
echo.
pause
