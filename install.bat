@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
title Valheim AutoModSync Installer

set "PACKAGEROOT=%~dp0"
set "AMS_VERSION="
if exist "%PACKAGEROOT%VERSION" set /p AMS_VERSION=<"%PACKAGEROOT%VERSION"
if not defined AMS_VERSION set "AMS_VERSION=unknown"
set "CLIENTDLL_PAYLOAD=%PACKAGEROOT%Client\ValheimAutoModSync.Client.dll"
set "CLIENTAPPLY_PAYLOAD=%PACKAGEROOT%Client\BepInEx\AutoModSync\ValheimAutoModSync.Apply.exe"
set "CLIENTRUNTIME=%PACKAGEROOT%Client"
set "LEGACY_BOOTSTRAP_SHA256=e5b15848829648dc97c7f40df2800c33372500e3b8047944ef1c84a2a107c3b8"
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
call :InstallClientRole
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
call :InstallClientRole
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
echo.
echo Auto-detecting Valheim...
if exist "%PACKAGEROOT%valheim.exe" set "DETECTEDROOT=%PACKAGEROOT:~0,-1%"
if defined DETECTEDROOT exit /b 0
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\Steam\steamapps\common\Valheim\valheim.exe" set "DETECTEDROOT=%%D:\Steam\steamapps\common\Valheim"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\SteamLibrary\steamapps\common\Valheim\valheim.exe" set "DETECTEDROOT=%%D:\SteamLibrary\steamapps\common\Valheim"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim.exe" set "DETECTEDROOT=%%D:\Program Files (x86)\Steam\steamapps\common\Valheim"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined DETECTEDROOT if exist "%%D:\Program Files\Steam\steamapps\common\Valheim\valheim.exe" set "DETECTEDROOT=%%D:\Program Files\Steam\steamapps\common\Valheim"
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

:InstallClientRole
echo.
echo Installing transparent AutoModSync client runtime...
if not exist "%CLIENTDLL_PAYLOAD%" goto :ClientPayloadMissing
if not exist "%CLIENTAPPLY_PAYLOAD%" goto :ClientPayloadMissing
if not exist "%CLIENTRUNTIME%\winhttp.dll" goto :ClientPayloadMissing
if not exist "%CLIENTRUNTIME%\doorstop_config.ini" goto :ClientPayloadMissing
if not exist "%CLIENTRUNTIME%\BepInEx\core\BepInEx.dll" goto :ClientPayloadMissing
if exist "%VALHEIMROOT%\version.dll" call :RemoveKnownLegacyBootstrap
if errorlevel 1 exit /b 1
if exist "%VALHEIMROOT%\BepInEx\core\BepInEx.dll" goto :ClientBepPresent
if exist "%VALHEIMROOT%\winhttp.dll" goto :ClientUnknownWinHttp
echo Installing BepInEx runtime files as normal visible files...
copy /y "%CLIENTRUNTIME%\winhttp.dll" "%VALHEIMROOT%\winhttp.dll" >nul 2>&1
if errorlevel 1 goto :ClientCopyFailed
copy /y "%CLIENTRUNTIME%\doorstop_config.ini" "%VALHEIMROOT%\doorstop_config.ini" >nul 2>&1
if errorlevel 1 goto :ClientCopyFailed
if not exist "%VALHEIMROOT%\BepInEx\core" mkdir "%VALHEIMROOT%\BepInEx\core" >nul 2>&1
xcopy "%CLIENTRUNTIME%\BepInEx\core\*" "%VALHEIMROOT%\BepInEx\core\" /E /I /Y /Q >nul
if errorlevel 1 goto :ClientCopyFailed
goto :ClientBepReady

:ClientBepPresent
echo Existing BepInEx installation detected and preserved.

:ClientBepReady
if not exist "%VALHEIMROOT%\BepInEx\plugins" mkdir "%VALHEIMROOT%\BepInEx\plugins" >nul 2>&1
if not exist "%VALHEIMROOT%\BepInEx\AutoModSync" mkdir "%VALHEIMROOT%\BepInEx\AutoModSync" >nul 2>&1
copy /y "%CLIENTDLL_PAYLOAD%" "%VALHEIMROOT%\BepInEx\plugins\ValheimAutoModSync.Client.dll" >nul 2>&1
if errorlevel 1 goto :ClientCopyFailed
copy /y "%CLIENTAPPLY_PAYLOAD%" "%VALHEIMROOT%\BepInEx\AutoModSync\ValheimAutoModSync.Apply.exe" >nul 2>&1
if errorlevel 1 goto :ClientCopyFailed
echo Installed AutoModSync client plugin and apply helper.
exit /b 0

:RemoveKnownLegacyBootstrap
if not exist "%BUILDTOOL%" goto :LegacyBootstrapUnknown
set "LEGACY_HASH_FILE=%TEMP%\ValheimAutoModSync_legacyhash_%RANDOM%_%RANDOM%.txt"
"%BUILDTOOL%" sha256 "%VALHEIMROOT%\version.dll" >"%LEGACY_HASH_FILE%" 2>nul
set "LEGACY_HASH="
if exist "%LEGACY_HASH_FILE%" set /p "LEGACY_HASH="<"%LEGACY_HASH_FILE%"
del /q "%LEGACY_HASH_FILE%" >nul 2>&1
if /i not "%LEGACY_HASH%"=="%LEGACY_BOOTSTRAP_SHA256%" goto :LegacyBootstrapUnknown
echo Removing legacy AutoModSync 2.4.4 packed version.dll...
del /f /q "%VALHEIMROOT%\version.dll" >nul 2>&1
if exist "%VALHEIMROOT%\version.dll" (
  echo ERROR: Could not remove the legacy AutoModSync version.dll.
  exit /b 1
)
echo Legacy packed bootstrap removed.
exit /b 0

:LegacyBootstrapUnknown
echo WARNING: Unknown version.dll found. AutoModSync will not delete or replace it:
echo   "%VALHEIMROOT%\version.dll"
exit /b 0

:ClientUnknownWinHttp
echo ERROR: winhttp.dll already exists, but BepInEx was not detected.
echo AutoModSync will not overwrite an unknown proxy DLL:
echo   "%VALHEIMROOT%\winhttp.dll"
exit /b 1

:ClientPayloadMissing
echo ERROR: This release package is missing transparent client runtime files.
exit /b 1

:ClientCopyFailed
echo ERROR: Failed to install the AutoModSync client runtime into:
echo   "%VALHEIMROOT%"
exit /b 1
:EnsureServerPayload
if not exist "%BUILDTOOL%" goto :RuntimePayloadMissing
if not exist "%SERVERDLL_PAYLOAD%" goto :RuntimePayloadMissing
if not exist "%CLIENTDLL_PAYLOAD%" goto :RuntimePayloadMissing
if not exist "%CLIENTAPPLY_PAYLOAD%" goto :RuntimePayloadMissing
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
rem 2.4.5+: publish only the normal BepInEx client plugin.
if exist "%RELEASEDIR%\version.dll" del /f /q "%RELEASEDIR%\version.dll" >nul 2>&1

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
