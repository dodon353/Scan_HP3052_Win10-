@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Dbus-Autostart.ps1"
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
