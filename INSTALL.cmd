@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Setup-All.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Setup failed. Error code: %RC%
pause
exit /b %RC%
