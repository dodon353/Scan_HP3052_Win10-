@echo off
setlocal
set "MODE=Enable"
if /I "%~1"=="off" set "MODE=Disable"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Dbus-Autostart.ps1" -Mode %MODE%
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
