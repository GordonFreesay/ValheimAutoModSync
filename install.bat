@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
title Valheim AutoModSync Installer

set "AMS_VERSION=2.4.4"
set "PACKAGEROOT=%~dp0"
set "CLIENTOUT=%PACKAGEROOT%Client\version.dll"
set "CLIENTDLL_PAYLOAD=%PACKAGEROOT%Client\ValheimAutoModSync.Client.dll"
set "SERVERDLL_PAYLOAD=%PACKAGEROOT%Server\ValheimAutoModSync.Server.dll"
set "BUILDTOOL=%PACKAGEROOT%Tools\AutoModSync.BuildTool.exe"
set "SERVERCFG_TEMPLATE=%PACKAGEROOT%Server\server-config-example.cfg"
set "BEPINEX_VERSION=5.4.2350"
set "BEPINEX_URL=https://gcdn.thunderstore.io/live/repository/packages/denikson-BepInExPack_Valheim-5.4.2350.zip"
set "BEPINEX_SHA256=37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f"
set "WORK="

echo.
echo ============================================================
echo          Valheim AutoModSync %AMS_VERSION% Installer
echo ============================================================
echo.
echo Close Valheim and any Valheim Dedicated Server before installing.
echo.
echo Choose installation type:
echo.
echo   1. Client
echo      Join AutoModSync-enabled servers.
echo.
echo   2. Dedicated Server
echo      Install on a standalone Valheim Dedicated Server.
echo.
echo   3. Host ^& Play
echo      Host a multiplayer world from this PC and play on it too.
echo.
choice /C 123 /N /M "Enter choice [1-3]: "
if errorlevel 3 goto :HostPlay
if errorlevel 2 goto :DedicatedServer
goto :RegularClient

:RegularClient
call :FindClientRoot
if errorlevel 1 goto :Fail
call :ConfirmClientRoot
if errorlevel 1 goto :Fail
call :InstallClientBootstrap
if errorlevel 1 goto :Fail
echo.
echo ============================================================
echo   CLIENT INSTALL COMPLETE
echo ============================================================
echo.
echo Launch Valheim normally through Steam.
goto :Success

:DedicatedServer
call :EnsureServerPayload
if errorlevel 1 goto :Fail
call :FindDedicatedRoot
if errorlevel 1 goto :Fail
call :ConfirmDedicatedRoot
if errorlevel 1 goto :Fail
set "TARGETROOT=%SERVERROOT%"
set "TARGETLABEL=Dedicated Server"
call :InstallServerRole
if errorlevel 1 goto :Fail
echo.
echo ============================================================
echo   DEDICATED SERVER INSTALL COMPLETE
echo ============================================================
echo.
echo Start the dedicated server normally.
goto :Success

:HostPlay
call :FindClientRoot
if errorlevel 1 goto :Fail
call :ConfirmClientRoot
if errorlevel 1 goto :Fail
call :InstallClientBootstrap
if errorlevel 1 goto :Fail
call :EnsureServerPayload
if errorlevel 1 goto :Fail
set "TARGETROOT=%VALHEIMROOT%"
set "TARGETLABEL=Host and Play"
call :InstallServerRole
if errorlevel 1 goto :Fail
echo.
echo ============================================================
echo   HOST ^& PLAY INSTALL COMPLETE
echo ============================================================
echo.
echo Launch Valheim normally through Steam.
echo When you host a world, AutoModSync will serve your BepInEx plugins.
goto :Success

:FindClientRoot
set "DETECTEDROOT="
set "DETECTFILE=%TEMP%\ValheimAutoModSync_client_%RANDOM%_%RANDOM%.txt"
set "AMS_INSTALLER_DIR=%PACKAGEROOT%"
echo.
echo Auto-detecting Valheim...
powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand JABFAHIAcgBvAHIAQQBjAHQAaQBvAG4AUAByAGUAZgBlAHIAZQBuAGMAZQA9ACcAUwBpAGwAZQBuAHQAbAB5AEMAbwBuAHQAaQBuAHUAZQAnAAoAJABjAGEAbgBkAGkAZABhAHQAZQBzAD0ATgBlAHcALQBPAGIAagBlAGMAdAAgAFMAeQBzAHQAZQBtAC4AQwBvAGwAbABlAGMAdABpAG8AbgBzAC4ARwBlAG4AZQByAGkAYwAuAEwAaQBzAHQAWwBzAHQAcgBpAG4AZwBdAAoAJABzAHQAZQBhAG0AUgBvAG8AdABzAD0ATgBlAHcALQBPAGIAagBlAGMAdAAgAFMAeQBzAHQAZQBtAC4AQwBvAGwAbABlAGMAdABpAG8AbgBzAC4ARwBlAG4AZQByAGkAYwAuAEwAaQBzAHQAWwBzAHQAcgBpAG4AZwBdAAoAZgB1AG4AYwB0AGkAbwBuACAAQQBkAGQALQBDAGEAbgBkAGkAZABhAHQAZQAoAFsAcwB0AHIAaQBuAGcAXQAkAHAAKQB7AGkAZgAoAC0AbgBvAHQAIABbAHMAdAByAGkAbgBnAF0AOgA6AEkAcwBOAHUAbABsAE8AcgBXAGgAaQB0AGUAUwBwAGEAYwBlACgAJABwACkAKQB7ACQAcwBjAHIAaQBwAHQAOgBjAGEAbgBkAGkAZABhAHQAZQBzAC4AQQBkAGQAKAAkAHAAKQB9AH0ACgBmAHUAbgBjAHQAaQBvAG4AIABBAGQAZAAtAFMAdABlAGEAbQBSAG8AbwB0ACgAWwBzAHQAcgBpAG4AZwBdACQAcAApAHsAaQBmACgAWwBzAHQAcgBpAG4AZwBdADoAOgBJAHMATgB1AGwAbABPAHIAVwBoAGkAdABlAFMAcABhAGMAZQAoACQAcAApACkAewByAGUAdAB1AHIAbgB9ADsAJABwAD0AJABwAC4AVAByAGkAbQAoACkALgBUAHIAaQBtACgAJwAiACcAKQAuAFIAZQBwAGwAYQBjAGUAKAAnAC8AJwAsACcAXABcACcAKQAuAFQAcgBpAG0ARQBuAGQAKAAnAFwAXAAnACkAOwBpAGYAKAAkAHMAYwByAGkAcAB0ADoAcwB0AGUAYQBtAFIAbwBvAHQAcwAgAC0AbgBvAHQAYwBvAG4AdABhAGkAbgBzACAAJABwACkAewAkAHMAYwByAGkAcAB0ADoAcwB0AGUAYQBtAFIAbwBvAHQAcwAuAEEAZABkACgAJABwACkAfQB9AAoAQQBkAGQALQBDAGEAbgBkAGkAZABhAHQAZQAgACQAZQBuAHYAOgBBAE0AUwBfAEkATgBTAFQAQQBMAEwARQBSAF8ARABJAFIACgAkAHAAZgA4ADYAPQBbAEUAbgB2AGkAcgBvAG4AbQBlAG4AdABdADoAOgBHAGUAdABFAG4AdgBpAHIAbwBuAG0AZQBuAHQAVgBhAHIAaQBhAGIAbABlACgAJwBQAHIAbwBnAHIAYQBtAEYAaQBsAGUAcwAoAHgAOAA2ACkAJwApADsAJABwAGYANgA0AD0AWwBFAG4AdgBpAHIAbwBuAG0AZQBuAHQAXQA6ADoARwBlAHQARQBuAHYAaQByAG8AbgBtAGUAbgB0AFYAYQByAGkAYQBiAGwAZQAoACcAUAByAG8AZwByAGEAbQBGAGkAbABlAHMAJwApAAoAaQBmACgAJABwAGYAOAA2ACkAewBBAGQAZAAtAFMAdABlAGEAbQBSAG8AbwB0ACAAKABKAG8AaQBuAC0AUABhAHQAaAAgACQAcABmADgANgAgACcAUwB0AGUAYQBtACcAKQB9ADsAaQBmACgAJABwAGYANgA0ACkAewBBAGQAZAAtAFMAdABlAGEAbQBSAG8AbwB0ACAAKABKAG8AaQBuAC0AUABhAHQAaAAgACQAcABmADYANAAgACcAUwB0AGUAYQBtACcAKQB9AAoAdAByAHkAewBBAGQAZAAtAFMAdABlAGEAbQBSAG8AbwB0ACAAKAAoAEcAZQB0AC0ASQB0AGUAbQBQAHIAbwBwAGUAcgB0AHkAIAAtAEwAaQB0AGUAcgBhAGwAUABhAHQAaAAgACcASABLAEMAVQA6AFwAUwBvAGYAdAB3AGEAcgBlAFwAVgBhAGwAdgBlAFwAUwB0AGUAYQBtACcAKQAuAFMAdABlAGEAbQBQAGEAdABoACkAfQBjAGEAdABjAGgAewB9AAoAdAByAHkAewBBAGQAZAAtAFMAdABlAGEAbQBSAG8AbwB0ACAAKAAoAEcAZQB0AC0ASQB0AGUAbQBQAHIAbwBwAGUAcgB0AHkAIAAtAEwAaQB0AGUAcgBhAGwAUABhAHQAaAAgACcASABLAEwATQA6AFwAUwBPAEYAVABXAEEAUgBFAFwAVwBPAFcANgA0ADMAMgBOAG8AZABlAFwAVgBhAGwAdgBlAFwAUwB0AGUAYQBtACcAKQAuAEkAbgBzAHQAYQBsAGwAUABhAHQAaAApAH0AYwBhAHQAYwBoAHsAfQAKAHQAcgB5AHsAQQBkAGQALQBTAHQAZQBhAG0AUgBvAG8AdAAgACgAKABHAGUAdAAtAEkAdABlAG0AUAByAG8AcABlAHIAdAB5ACAALQBMAGkAdABlAHIAYQBsAFAAYQB0AGgAIAAnAEgASwBMAE0AOgBcAFMATwBGAFQAVwBBAFIARQBcAFYAYQBsAHYAZQBcAFMAdABlAGEAbQAnACkALgBJAG4AcwB0AGEAbABsAFAAYQB0AGgAKQB9AGMAYQB0AGMAaAB7AH0ACgBmAG8AcgBlAGEAYwBoACgAJAByAG8AbwB0ACAAaQBuACAAQAAoACQAcwB0AGUAYQBtAFIAbwBvAHQAcwApACkAewBBAGQAZAAtAEMAYQBuAGQAaQBkAGEAdABlACAAKABKAG8AaQBuAC0AUABhAHQAaAAgACQAcgBvAG8AdAAgACcAcwB0AGUAYQBtAGEAcABwAHMAXABjAG8AbQBtAG8AbgBcAFYAYQBsAGgAZQBpAG0AJwApADsAJAB2AGQAZgA9AEoAbwBpAG4ALQBQAGEAdABoACAAJAByAG8AbwB0ACAAJwBzAHQAZQBhAG0AYQBwAHAAcwBcAGwAaQBiAHIAYQByAHkAZgBvAGwAZABlAHIAcwAuAHYAZABmACcAOwBpAGYAKABUAGUAcwB0AC0AUABhAHQAaAAgAC0ATABpAHQAZQByAGEAbABQAGEAdABoACAAJAB2AGQAZgApAHsAdAByAHkAewAkAHQAZQB4AHQAPQBbAEkATwAuAEYAaQBsAGUAXQA6ADoAUgBlAGEAZABBAGwAbABUAGUAeAB0ACgAJAB2AGQAZgApADsAZgBvAHIAZQBhAGMAaAAoACQAbQAgAGkAbgAgAFsAcgBlAGcAZQB4AF0AOgA6AE0AYQB0AGMAaABlAHMAKAAkAHQAZQB4AHQALAAnACIAcABhAHQAaAAiAFwAcwArACIAKABbAF4AXAAiAF0AKwApACIAJwApACkAewAkAGwAaQBiAD0AJABtAC4ARwByAG8AdQBwAHMAWwAxAF0ALgBWAGEAbAB1AGUAIAAtAHIAZQBwAGwAYQBjAGUAIAAnAFwAXABcAFwAJwAsACcAXABcACcAOwBBAGQAZAAtAEMAYQBuAGQAaQBkAGEAdABlACAAKABKAG8AaQBuAC0AUABhAHQAaAAgACQAbABpAGIAIAAnAHMAdABlAGEAbQBhAHAAcABzAFwAYwBvAG0AbQBvAG4AXABWAGEAbABoAGUAaQBtACcAKQB9AH0AYwBhAHQAYwBoAHsAfQB9AH0ACgBmAG8AcgBlAGEAYwBoACgAJABkACAAaQBuACAAWwBJAE8ALgBEAHIAaQB2AGUASQBuAGYAbwBdADoAOgBHAGUAdABEAHIAaQB2AGUAcwAoACkAKQB7AHQAcgB5AHsAaQBmACgALQBuAG8AdAAgACQAZAAuAEkAcwBSAGUAYQBkAHkAKQB7AGMAbwBuAHQAaQBuAHUAZQB9ADsAJAByAD0AJABkAC4AUgBvAG8AdABEAGkAcgBlAGMAdABvAHIAeQAuAEYAdQBsAGwATgBhAG0AZQA7AEEAZABkAC0AQwBhAG4AZABpAGQAYQB0AGUAIAAoAEoAbwBpAG4ALQBQAGEAdABoACAAJAByACAAJwBTAHQAZQBhAG0ATABpAGIAcgBhAHIAeQBcAHMAdABlAGEAbQBhAHAAcABzAFwAYwBvAG0AbQBvAG4AXABWAGEAbABoAGUAaQBtACcAKQA7AEEAZABkAC0AQwBhAG4AZABpAGQAYQB0AGUAIAAoAEoAbwBpAG4ALQBQAGEAdABoACAAJAByACAAJwBTAHQAZQBhAG0AXABzAHQAZQBhAG0AYQBwAHAAcwBcAGMAbwBtAG0AbwBuAFwAVgBhAGwAaABlAGkAbQAnACkAfQBjAGEAdABjAGgAewB9AH0ACgAkAHMAZQBlAG4APQBAAHsAfQA7AGYAbwByAGUAYQBjAGgAKAAkAGMAIABpAG4AIAAkAGMAYQBuAGQAaQBkAGEAdABlAHMAKQB7AGkAZgAoAFsAcwB0AHIAaQBuAGcAXQA6ADoASQBzAE4AdQBsAGwATwByAFcAaABpAHQAZQBTAHAAYQBjAGUAKAAkAGMAKQApAHsAYwBvAG4AdABpAG4AdQBlAH0AOwB0AHIAeQB7ACQAZgA9AFsASQBPAC4AUABhAHQAaABdADoAOgBHAGUAdABGAHUAbABsAFAAYQB0AGgAKAAkAGMALgBUAHIAaQBtAEUAbgBkACgAJwBcAFwAJwApACkAfQBjAGEAdABjAGgAewBjAG8AbgB0AGkAbgB1AGUAfQA7ACQAawA9ACQAZgAuAFQAbwBMAG8AdwBlAHIASQBuAHYAYQByAGkAYQBuAHQAKAApADsAaQBmACgAJABzAGUAZQBuAC4AQwBvAG4AdABhAGkAbgBzAEsAZQB5ACgAJABrACkAKQB7AGMAbwBuAHQAaQBuAHUAZQB9ADsAJABzAGUAZQBuAFsAJABrAF0APQAkAHQAcgB1AGUAOwBpAGYAKABUAGUAcwB0AC0AUABhAHQAaAAgAC0ATABpAHQAZQByAGEAbABQAGEAdABoACAAKABKAG8AaQBuAC0AUABhAHQAaAAgACQAZgAgACcAdgBhAGwAaABlAGkAbQAuAGUAeABlACcAKQApAHsAWwBDAG8AbgBzAG8AbABlAF0AOgA6AE8AdQB0AC4AVwByAGkAdABlAEwAaQBuAGUAKAAkAGYAKQA7AGUAeABpAHQAIAAwAH0AfQAKAGUAeABpAHQAIAAxAA== >"%DETECTFILE%" 2>nul
if exist "%DETECTFILE%" set /p "DETECTEDROOT="<"%DETECTFILE%"
del /q "%DETECTFILE%" >nul 2>&1
set "AMS_INSTALLER_DIR="
exit /b 0

:ConfirmClientRoot
echo.
if not defined DETECTEDROOT goto :AskClientRoot
echo Valheim path: "%DETECTEDROOT%"
echo If correct, hit Enter.
echo Do NOT use quotation marks if entering a different path.
echo.
set "VALHEIMROOT="
set /p "VALHEIMROOT=If not, enter Valheim client path: "
if not defined VALHEIMROOT set "VALHEIMROOT=%DETECTEDROOT%"
goto :ValidateClientRoot

:AskClientRoot
echo Auto-detection could not find Valheim.
echo Enter the full folder containing valheim.exe.
echo Do NOT use quotation marks.
echo.
set "VALHEIMROOT="
set /p "VALHEIMROOT=Enter Valheim client path: "

:ValidateClientRoot
set "VALHEIMROOT=%VALHEIMROOT:"=%"
if not defined VALHEIMROOT goto :ClientPathMissing
pushd "%VALHEIMROOT%" >nul 2>&1
if errorlevel 1 goto :ClientPathBad
set "VALHEIMROOT=%CD%"
popd
if not exist "%VALHEIMROOT%\valheim.exe" goto :ClientPathBad
echo.
echo Verified Valheim path:
echo   "%VALHEIMROOT%"
exit /b 0

:ClientPathMissing
echo ERROR: No Valheim path was provided.
exit /b 1

:ClientPathBad
echo ERROR: valheim.exe was not found in the selected folder.
exit /b 1

:InstallClientBootstrap
if not exist "%CLIENTOUT%" goto :ClientPayloadMissing
set "DEST_DLL=%VALHEIMROOT%\version.dll"
if not exist "%DEST_DLL%" goto :CopyClientBootstrap
fc /b "%CLIENTOUT%" "%DEST_DLL%" >nul 2>&1
if not errorlevel 1 goto :ClientAlreadyCurrent
echo.
echo WARNING: A different version.dll already exists in the Valheim folder.
echo It may be an older AutoModSync version or another proxy/mod loader.
echo AutoModSync needs this filename. The existing file will be backed up.
echo.
choice /C YN /N /M "Continue and replace it? [Y/N]: "
if errorlevel 2 exit /b 1
set "BACKUP_DLL=%VALHEIMROOT%\version.dll.pre-AutoModSync-%RANDOM%-%RANDOM%.bak"
copy /y "%DEST_DLL%" "%BACKUP_DLL%" >nul 2>&1
if errorlevel 1 goto :ClientBackupFailed
echo Existing version.dll backed up to:
echo   "%BACKUP_DLL%"

:CopyClientBootstrap
copy /y "%CLIENTOUT%" "%DEST_DLL%" >nul 2>&1
if errorlevel 1 goto :ClientCopyFailed
echo Installed AutoModSync client bootstrap:
echo   "%DEST_DLL%"
exit /b 0

:ClientAlreadyCurrent
echo AutoModSync client bootstrap is already current.
exit /b 0

:ClientPayloadMissing
echo ERROR: Client\version.dll is missing from this release package.
exit /b 1

:ClientBackupFailed
echo ERROR: Could not back up the existing version.dll.
exit /b 1

:ClientCopyFailed
echo ERROR: Could not copy version.dll into the Valheim folder.
exit /b 1

:EnsureServerPayload
if not exist "%BUILDTOOL%" goto :RuntimePayloadMissing
if not exist "%SERVERDLL_PAYLOAD%" goto :RuntimePayloadMissing
if not exist "%CLIENTDLL_PAYLOAD%" goto :RuntimePayloadMissing
if not defined WORK set "WORK=%TEMP%\ValheimAutoModSync_%RANDOM%_%RANDOM%"
if not exist "%WORK%" mkdir "%WORK%" >nul 2>&1
exit /b 0

:RuntimePayloadMissing
echo ERROR: This release package is missing prebuilt AutoModSync runtime files.
echo Re-download or re-extract the complete release.
exit /b 1


:FindDedicatedRoot
set "DETECTEDROOT="
echo.
echo Auto-detecting Valheim Dedicated Server...

rem Package is sometimes extracted directly inside the dedicated-server folder.
if exist "%PACKAGEROOT%valheim_server.exe" set "DETECTEDROOT=%PACKAGEROOT:~0,-1%"
if defined DETECTEDROOT exit /b 0

rem Fast, deterministic scan of common layouts on every drive.
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\Steam\steamapps\common\Valheim dedicated server\valheim_server.exe" set "DETECTEDROOT=%%D:\Steam\steamapps\common\Valheim dedicated server"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\SteamLibrary\steamapps\common\Valheim dedicated server\valheim_server.exe" set "DETECTEDROOT=%%D:\SteamLibrary\steamapps\common\Valheim dedicated server"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\SteamCMD\steamapps\common\Valheim dedicated server\valheim_server.exe" set "DETECTEDROOT=%%D:\SteamCMD\steamapps\common\Valheim dedicated server"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\ValheimServer\valheim_server.exe" set "DETECTEDROOT=%%D:\ValheimServer"
if defined DETECTEDROOT exit /b 0

rem Fallback to the packaged detector for unusual Steam-library locations.
if not exist "%BUILDTOOL%" exit /b 0
set "DETECTSERVER=%TEMP%\ValheimAutoModSync_server_%RANDOM%_%RANDOM%.txt"
"%BUILDTOOL%" findserver "%PACKAGEROOT%" >"%DETECTSERVER%" 2>"%TEMP%\ValheimAutoModSync_findserver.err"
if exist "%DETECTSERVER%" set /p "DETECTEDROOT="<"%DETECTSERVER%"
del /q "%DETECTSERVER%" >nul 2>&1
exit /b 0

:ConfirmDedicatedRoot
echo.
if not defined DETECTEDROOT goto :AskDedicatedRoot
echo Server path: "%DETECTEDROOT%"
echo If correct, hit Enter.
echo Do NOT use quotation marks if entering a different path.
echo.
set "SERVERROOT="
set /p "SERVERROOT=If not, enter Valheim server path: "
if not defined SERVERROOT set "SERVERROOT=%DETECTEDROOT%"
goto :ValidateDedicatedRoot

:AskDedicatedRoot
echo Auto-detection could not find Valheim Dedicated Server.
echo Enter the full folder containing valheim_server.exe.
echo Do NOT use quotation marks.
echo.
set "SERVERROOT="
set /p "SERVERROOT=Enter Valheim server path: "

:ValidateDedicatedRoot
set "SERVERROOT=%SERVERROOT:"=%"
if not defined SERVERROOT goto :DedicatedPathMissing
pushd "%SERVERROOT%" >nul 2>&1
if errorlevel 1 goto :DedicatedPathBad
set "SERVERROOT=%CD%"
popd
if not exist "%SERVERROOT%\valheim_server.exe" goto :DedicatedPathBad
echo.
echo Verified server path:
echo   "%SERVERROOT%"
exit /b 0

:DedicatedPathMissing
echo ERROR: No dedicated server path was provided.
exit /b 1

:DedicatedPathBad
echo ERROR: valheim_server.exe was not found in the selected folder.
exit /b 1

:InstallServerRole
echo.
echo Installing AutoModSync server role for %TARGETLABEL%...
if exist "%TARGETROOT%\BepInEx\core\BepInEx.dll" goto :ServerBepPresent
call :InstallBepInEx
if errorlevel 1 exit /b 1
goto :ServerBepReady

:ServerBepPresent
echo BepInEx detected. Existing installation will be preserved.

:ServerBepReady
if not exist "%TARGETROOT%\BepInEx\core\BepInEx.dll" goto :ServerBepMissing
set "PLUGIN_DIR=%TARGETROOT%\BepInEx\plugins"
set "CONFIG_DIR=%TARGETROOT%\BepInEx\config"
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%" >nul 2>&1
if not exist "%CONFIG_DIR%" mkdir "%CONFIG_DIR%" >nul 2>&1
set "IDENTITY_PRIVATE=%CONFIG_DIR%\ValheimAutoModSync.private.xml"
set "IDENTITY_PUBLIC=%CONFIG_DIR%\ValheimAutoModSync.public.xml"
"%BUILDTOOL%" identity "%IDENTITY_PRIVATE%" "%IDENTITY_PUBLIC%"
if errorlevel 1 exit /b 1
set "SERVERDLL=%PLUGIN_DIR%\ValheimAutoModSync.Server.dll"
copy /y "%SERVERDLL_PAYLOAD%" "%SERVERDLL%" >nul 2>&1
if errorlevel 1 goto :ServerPluginCopyFailed
set "SERVERCFG=%CONFIG_DIR%\com.gordonfreesay.valheimautomodsync.server.cfg"
if exist "%SERVERCFG%" goto :ServerConfigExists
copy /y "%SERVERCFG_TEMPLATE%" "%SERVERCFG%" >nul 2>&1
if errorlevel 1 goto :ServerConfigCopyFailed
echo Installed default server config:
echo   "%SERVERCFG%"
goto :ServerConfigReady

:ServerConfigExists
echo Existing server config preserved:
echo   "%SERVERCFG%"

:ServerConfigReady
set "RELEASEDIR=%TARGETROOT%\BepInEx\AutoModSync\release"
if not exist "%RELEASEDIR%" mkdir "%RELEASEDIR%" >nul 2>&1
copy /y "%CLIENTDLL_PAYLOAD%" "%RELEASEDIR%\ValheimAutoModSync.Client.dll" >nul 2>&1
if errorlevel 1 goto :ReleaseCopyFailed
if not exist "%CLIENTOUT%" goto :NoBootstrapForRelease
copy /y "%CLIENTOUT%" "%RELEASEDIR%\version.dll" >nul 2>&1
if errorlevel 1 goto :ReleaseCopyFailed
goto :ServerRoleComplete

:NoBootstrapForRelease
echo WARNING: Client\version.dll is not present. Bootstrap self-updates are unavailable.

:ServerRoleComplete
echo AutoModSync server role installed:
echo   "%SERVERDLL%"
exit /b 0

:ServerBepMissing
echo ERROR: BepInEx installation did not produce BepInEx.dll.
exit /b 1

:ServerPluginCopyFailed
echo ERROR: Could not install the prebuilt AutoModSync server plugin.
exit /b 1

:ServerConfigCopyFailed
echo ERROR: Could not install the AutoModSync server config.
exit /b 1

:ReleaseCopyFailed
echo ERROR: Could not install the AutoModSync client update payload.
exit /b 1

:InstallBepInEx
echo BepInEx was not detected. Installing BepInExPack Valheim %BEPINEX_VERSION%...
if exist "%TARGETROOT%\winhttp.dll" goto :UnknownWinHttp
call :PreparePinnedBepInExSource
if errorlevel 1 exit /b 1
xcopy "%BEPSOURCE%\*" "%TARGETROOT%\" /E /I /Y /Q >nul
if errorlevel 1 goto :BepCopyFailed
if not exist "%TARGETROOT%\BepInEx\core\BepInEx.dll" goto :BepCopyFailed
echo BepInEx installed successfully.
exit /b 0

:UnknownWinHttp
echo ERROR: winhttp.dll already exists, but BepInEx was not detected.
echo AutoModSync will not overwrite an unknown proxy DLL:
echo   "%TARGETROOT%\winhttp.dll"
exit /b 1

:BepCopyFailed
echo ERROR: Failed to install BepInEx into:
echo   "%TARGETROOT%"
exit /b 1

:PreparePinnedBepInExSource
if defined BEPSOURCE if exist "%BEPSOURCE%\BepInEx\core\BepInEx.dll" exit /b 0
set "BEPZIP=%WORK%\BepInExPack_Valheim-%BEPINEX_VERSION%.zip"
set "BEPEXTRACT=%WORK%\BepInExExtract"
set "BUNDLED_BEPZIP=%PACKAGEROOT%Bundled\BepInExPack_Valheim-%BEPINEX_VERSION%.zip"
if exist "%BEPZIP%" goto :VerifyBepZip
if exist "%BUNDLED_BEPZIP%" goto :UseBundledBepZip
where curl.exe >nul 2>&1
if errorlevel 1 goto :NoCurl
echo Downloading pinned Valheim BepInEx package from Thunderstore...
curl.exe -L --fail --retry 3 --retry-delay 2 -o "%BEPZIP%" "%BEPINEX_URL%"
if errorlevel 1 goto :BepDownloadFailed
goto :VerifyBepZip

:UseBundledBepZip
copy /y "%BUNDLED_BEPZIP%" "%BEPZIP%" >nul 2>&1
if errorlevel 1 goto :BepDownloadFailed

:VerifyBepZip
set "ACTUALSHA="
"%BUILDTOOL%" sha256 "%BEPZIP%" >"%WORK%\bep-sha.txt" 2>"%WORK%\bep-sha-error.txt"
if errorlevel 1 goto :BepHashFailed
set /p "ACTUALSHA="<"%WORK%\bep-sha.txt"
if not defined ACTUALSHA goto :BepHashFailed
if /i not "%ACTUALSHA%"=="%BEPINEX_SHA256%" goto :BepHashMismatch
echo SHA-256 verified.
if exist "%BEPEXTRACT%" rmdir /s /q "%BEPEXTRACT%" >nul 2>&1
mkdir "%BEPEXTRACT%" >nul 2>&1
where tar.exe >nul 2>&1
if errorlevel 1 goto :ExtractBepPowerShell
tar.exe -xf "%BEPZIP%" -C "%BEPEXTRACT%"
if errorlevel 1 goto :BepExtractFailed
goto :BepExtracted

:ExtractBepPowerShell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%BEPZIP%' -DestinationPath '%BEPEXTRACT%' -Force"
if errorlevel 1 goto :BepExtractFailed

:BepExtracted
set "BEPSOURCE=%BEPEXTRACT%\BepInExPack_Valheim"
if not exist "%BEPSOURCE%\BepInEx\core\BepInEx.dll" goto :BepExtractFailed
if not exist "%BEPSOURCE%\winhttp.dll" goto :BepExtractFailed
if not exist "%BEPSOURCE%\doorstop_config.ini" goto :BepExtractFailed
exit /b 0

:NoCurl
echo ERROR: curl.exe was not found and no bundled BepInEx archive is available.
exit /b 1

:BepDownloadFailed
echo ERROR: BepInEx download/copy failed.
exit /b 1

:BepHashFailed
echo ERROR: Could not calculate BepInEx SHA-256.
exit /b 1

:BepHashMismatch
echo ERROR: BepInEx SHA-256 verification failed.
echo Expected: %BEPINEX_SHA256%
echo Actual:   %ACTUALSHA%
exit /b 1

:BepExtractFailed
echo ERROR: Could not extract the pinned BepInEx package.
exit /b 1

:Success
if defined WORK if exist "%WORK%" rmdir /s /q "%WORK%" >nul 2>&1
echo.
pause
exit /b 0

:Fail
if defined WORK if exist "%WORK%" rmdir /s /q "%WORK%" >nul 2>&1
echo.
echo Installation failed.
echo.
pause
exit /b 1
