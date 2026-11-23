@echo off
rem Install.cmd - installs or upgrades RTC Call Monitor (asks for administrator rights)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Install.ps1" %*
echo.
pause
