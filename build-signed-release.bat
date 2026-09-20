@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Valheim AutoModSync Signed Release Builder

set "HAS_SIGNING_CONFIG="
if defined AMS_ARTIFACT_SIGNING_DLIB set "HAS_SIGNING_CONFIG=1"
if defined AMS_SIGN_PFX set "HAS_SIGNING_CONFIG=1"
if defined AMS_SIGN_THUMBPRINT set "HAS_SIGNING_CONFIG=1"

if not defined HAS_SIGNING_CONFIG (
  echo.
  echo ERROR: No trusted code-signing identity is configured.
  echo See SIGNING.md.
  echo.
  pause
  exit /b 1
)

set "AMS_REQUIRE_SIGNING=1"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-thunderstore.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
  echo Signed unified release build failed with exit code %RC%.
  pause
  exit /b %RC%
)

echo ============================================================
echo   SIGNED RELEASE BUILD COMPLETE
echo ============================================================
echo.
echo Both release formats are under Dist.
echo Signing and verification were mandatory for this build.
echo.
pause
exit /b 0
