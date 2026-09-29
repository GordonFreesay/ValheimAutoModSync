@echo off
setlocal
cd /d "%~dp0"

rem Compatibility launcher only. All install/repair/uninstall logic lives in the signed ValheimAutoModSyncInstaller.exe.
rem The installer performs no runtime mod download and uses normal Windows UAC only for explicit installation actions.
if not exist "%~dp0ValheimAutoModSyncInstaller.exe" (
  echo ERROR: ValheimAutoModSyncInstaller.exe is missing from this release package.
  exit /b 1
)

"%~dp0ValheimAutoModSyncInstaller.exe"
exit /b %ERRORLEVEL%
