@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Install-Ubuntu.ps1"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="3010" echo Restart Windows, then run INSTALL-UBUNTU.cmd again.
if not "%RC%"=="0" if not "%RC%"=="3010" echo Ubuntu setup failed. Error code: %RC%
pause
exit /b %RC%
