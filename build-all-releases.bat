@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-store-packages.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Distribution package build failed with exit code %RC%.
if "%RC%"=="0" echo Standalone, Nexus, CurseForge, and Thunderstore packages are under Dist.
pause
exit /b %RC%
