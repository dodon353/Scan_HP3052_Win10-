@echo off
cd /d "%~dp0"
if not exist "%~dp0app\HP3052Scanner.exe" (
  echo HP3052Scanner.exe was not found.
  echo Extract the entire program archive to a permanent folder, then run START.cmd again.
  pause
  exit /b 1
)
start "" /D "%~dp0app" "%~dp0app\HP3052Scanner.exe"
exit /b %ERRORLEVEL%
