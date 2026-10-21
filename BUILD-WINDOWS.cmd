@echo off
cd /d "%~dp0"
set "SCANNER_SELF_CONTAINED=false"
set "SCANNER_COMPRESS=false"
if /I "%~1"=="portable" (
  set "SCANNER_SELF_CONTAINED=true"
  set "SCANNER_COMPRESS=true"
)
dotnet publish "src\HP3052Scanner.csproj" -c Release -r win-x64 --self-contained %SCANNER_SELF_CONTAINED% -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=%SCANNER_COMPRESS% -p:IncludeNativeLibrariesForSelfExtract=%SCANNER_SELF_CONTAINED% -o "app"
if errorlevel 1 (
  echo Build failed. Read the error above.
  pause
  exit /b 1
)
echo Build finished. Run START.cmd.
pause
