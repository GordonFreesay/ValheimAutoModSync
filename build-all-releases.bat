@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-thunderstore.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Unified release build failed with exit code %RC%.
if "%RC%"=="0" echo Both release formats are under Dist.
pause
exit /b %RC%