@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
title Valheim AutoModSync Release Builder

set "ROOT=%~dp0"
set "AMS_VERSION="
if not exist "%ROOT%VERSION" goto :MissingVersion
set /p AMS_VERSION=<"%ROOT%VERSION"
if not defined AMS_VERSION goto :MissingVersion
set "SOURCE=%ROOT%Source"
set "CLIENTDIR=%ROOT%Client"
set "SERVERDIR=%ROOT%Server"
set "TOOLSDIR=%ROOT%Tools"
set "DIST=%ROOT%Dist"
set "BRANDING_PNG=%ROOT%Thunderstore\icon.png"
set "BRANDING_SCRIPT=%ROOT%build-branding-assets.ps1"
set "BEPINEX_VERSION=5.4.2350"
set "BEPINEX_URL=https://gcdn.thunderstore.io/live/repository/packages/denikson-BepInExPack_Valheim-5.4.2350.zip"
set "BEPINEX_SHA256=37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f"
set "SIGNING_STATUS=UNSIGNED"

echo.
echo ============================================================
echo   Valheim AutoModSync %AMS_VERSION% - Transparent Build
 echo ============================================================
echo.

set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC goto :NoCompiler

if exist "%ROOT%verify-source-docs.ps1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%verify-source-docs.ps1"
  if errorlevel 1 goto :FailNoWork
)
if exist "%ROOT%verify-version.ps1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%verify-version.ps1"
  if errorlevel 1 goto :FailNoWork
)

set "WORK=%TEMP%\ValheimAutoModSync_Build_%RANDOM%_%RANDOM%"
mkdir "%WORK%" >nul 2>&1
if not exist "%CLIENTDIR%" mkdir "%CLIENTDIR%" >nul 2>&1
if not exist "%SERVERDIR%" mkdir "%SERVERDIR%" >nul 2>&1
if not exist "%TOOLSDIR%" mkdir "%TOOLSDIR%" >nul 2>&1

set "BUILDTOOL=%WORK%\AutoModSync.BuildTool.exe"
"%CSC%" /nologo /target:exe /optimize+ /langversion:5 /out:"%BUILDTOOL%" "%SOURCE%\AutoModSync.BuildTool.cs"
if errorlevel 1 goto :Fail
copy /y "%BUILDTOOL%" "%TOOLSDIR%\AutoModSync.BuildTool.exe" >nul
if errorlevel 1 goto :Fail

set "VALHEIMROOT=%AMS_VALHEIMROOT%"
if defined VALHEIMROOT set "VALHEIMROOT=%VALHEIMROOT:"=%"
if not defined VALHEIMROOT if exist "%ProgramFiles(x86)%\Steam\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%ProgramFiles(x86)%\Steam\steamapps\common\Valheim"
if not defined VALHEIMROOT if exist "%ProgramFiles%\Steam\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%ProgramFiles%\Steam\steamapps\common\Valheim"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined VALHEIMROOT if exist "%%D:\SteamLibrary\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%%D:\SteamLibrary\steamapps\common\Valheim"
if not defined VALHEIMROOT (
  echo Enter the Valheim or Valheim Dedicated Server installation folder.
  set /p "VALHEIMROOT=Valheim path: "
)
set "VALHEIMROOT=%VALHEIMROOT:"=%"
set "VALHEIMMANAGED=%VALHEIMROOT%\valheim_Data\Managed"
if not exist "%VALHEIMMANAGED%\assembly_valheim.dll" set "VALHEIMMANAGED=%VALHEIMROOT%\valheim_server_Data\Managed"
if not exist "%VALHEIMMANAGED%\assembly_valheim.dll" goto :BadClient
if not exist "%VALHEIMROOT%\valheim.exe" if not exist "%VALHEIMROOT%\valheim_server.exe" goto :BadClient

echo Valheim path: "%VALHEIMROOT%"
echo Managed references: "%VALHEIMMANAGED%"
call :PreparePinnedBepInEx
if errorlevel 1 goto :Fail

set "BEPINEX_DLL=%BEPSOURCE%\BepInEx\core\BepInEx.dll"
set "HARMONY_DLL=%BEPSOURCE%\BepInEx\core\0Harmony.dll"
set "GAME_DLL=%VALHEIMMANAGED%\assembly_valheim.dll"
set "ASSEMBLY_UTILS=%VALHEIMMANAGED%\assembly_utils.dll"
set "SPLATFORM_DLL=%VALHEIMMANAGED%\Splatform.dll"
set "STEAMWORKS_DLL=%VALHEIMMANAGED%\com.rlabrecque.steamworks.net.dll"
set "NETSTANDARD_DLL=%VALHEIMMANAGED%\netstandard.dll"
set "UNITY_ENGINE=%BEPSOURCE%\unstripped_corlib\UnityEngine.dll"
if not exist "%UNITY_ENGINE%" set "UNITY_ENGINE=%VALHEIMMANAGED%\UnityEngine.dll"
set "UNITY_CORE=%BEPSOURCE%\unstripped_corlib\UnityEngine.CoreModule.dll"
if not exist "%UNITY_CORE%" set "UNITY_CORE=%VALHEIMMANAGED%\UnityEngine.CoreModule.dll"
set "UNITY_IMGUI=%BEPSOURCE%\unstripped_corlib\UnityEngine.IMGUIModule.dll"
if not exist "%UNITY_IMGUI%" set "UNITY_IMGUI=%VALHEIMMANAGED%\UnityEngine.IMGUIModule.dll"
set "UNITY_TEXT=%BEPSOURCE%\unstripped_corlib\UnityEngine.TextRenderingModule.dll"
if not exist "%UNITY_TEXT%" set "UNITY_TEXT=%VALHEIMMANAGED%\UnityEngine.TextRenderingModule.dll"
if not exist "%BRANDING_PNG%" (
  echo ERROR: AutoModSync branding PNG was not found at "%BRANDING_PNG%".
  goto :Fail
)
if not exist "%BRANDING_SCRIPT%" (
  echo ERROR: build-branding-assets.ps1 is missing.
  goto :Fail
)

set "APPLYICO=%WORK%\ValheimAutoModSync.Apply.ico"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%BRANDING_SCRIPT%" -SourcePng "%BRANDING_PNG%" -OutputIco "%APPLYICO%"
if errorlevel 1 goto :Fail

set "REFS=%WORK%\refs.rsp"
>"%REFS%" echo /nologo
>>"%REFS%" echo /optimize+
>>"%REFS%" echo /langversion:5
>>"%REFS%" echo /reference:"%BEPINEX_DLL%"
>>"%REFS%" echo /reference:"%HARMONY_DLL%"
>>"%REFS%" echo /reference:"%GAME_DLL%"
>>"%REFS%" echo /reference:"%UNITY_ENGINE%"
>>"%REFS%" echo /reference:"%UNITY_CORE%"
>>"%REFS%" echo /reference:"%UNITY_IMGUI%"
>>"%REFS%" echo /reference:"%UNITY_TEXT%"
>>"%REFS%" echo /reference:"%SPLATFORM_DLL%"
>>"%REFS%" echo /reference:"%STEAMWORKS_DLL%"
>>"%REFS%" echo /reference:"%NETSTANDARD_DLL%"
>>"%REFS%" echo /reference:System.IO.Compression.dll
>>"%REFS%" echo /reference:System.IO.Compression.FileSystem.dll
if exist "%ASSEMBLY_UTILS%" >>"%REFS%" echo /reference:"%ASSEMBLY_UTILS%"

set "CLIENTDLL=%WORK%\ValheimAutoModSync.Client.dll"
set "SERVERDLL=%WORK%\ValheimAutoModSync.Server.dll"
set "APPLYEXE=%WORK%\ValheimAutoModSync.Apply.exe"
set "INSTALLEREXE=%WORK%\ValheimAutoModSyncInstaller.exe"
echo Compiling AutoModSync components...
"%CSC%" @"%REFS%" /target:library /resource:"%BRANDING_PNG%",ValheimAutoModSync.Branding.Logo.png /out:"%CLIENTDLL%" "%SOURCE%\ValheimAutoModSync.Client.cs" "%SOURCE%\AutoModSync.SyncUiState.cs" "%SOURCE%\AutoModSync.PathSafety.cs" "%SOURCE%\AutoModSync.ClientResourceSafety.cs" "%SOURCE%\AutoModSync.ResumeState.cs" "%SOURCE%\AutoModSync.OwnershipState.cs"
if errorlevel 1 goto :Fail
"%CSC%" @"%REFS%" /target:library /out:"%SERVERDLL%" "%SOURCE%\ValheimAutoModSync.Server.cs" "%SOURCE%\AutoModSync.PathSafety.cs" "%SOURCE%\AutoModSync.ManifestScanner.cs" "%SOURCE%\AutoModSync.ServerResourceSafety.cs" "%SOURCE%\AutoModSync.ResumeState.cs" "%SOURCE%\AutoModSync.TransferScheduler.cs" "%SOURCE%\AutoModSync.ClientPayload.cs"
if errorlevel 1 goto :Fail
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /win32icon:"%APPLYICO%" /out:"%APPLYEXE%" "%SOURCE%\ValheimAutoModSync.Apply.cs" "%SOURCE%\AutoModSync.PathSafety.cs" "%SOURCE%\AutoModSync.OwnershipState.cs"
if errorlevel 1 goto :Fail
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /win32manifest:"%SOURCE%\AutoModSyncInstaller.manifest" /win32icon:"%APPLYICO%" /out:"%INSTALLEREXE%" "%SOURCE%\ValheimAutoModSync.Installer.cs"
if errorlevel 1 goto :Fail

rem Sign AutoModSync-authored PE files before they are copied or packaged.
call :SignReleaseBinaries
if errorlevel 1 goto :Fail
copy /y "%BUILDTOOL%" "%TOOLSDIR%\AutoModSync.BuildTool.exe" >nul
if errorlevel 1 goto :Fail

rem Build a normal, transparent client layout. No packed version.dll.
if exist "%CLIENTDIR%\BepInEx" rmdir /s /q "%CLIENTDIR%\BepInEx"
if exist "%CLIENTDIR%\version.dll" del /f /q "%CLIENTDIR%\version.dll"
if exist "%CLIENTDIR%\version.template.dll" del /f /q "%CLIENTDIR%\version.template.dll"
copy /y "%BEPSOURCE%\winhttp.dll" "%CLIENTDIR%\winhttp.dll" >nul
copy /y "%BEPSOURCE%\doorstop_config.ini" "%CLIENTDIR%\doorstop_config.ini" >nul
mkdir "%CLIENTDIR%\BepInEx\core" >nul 2>&1
mkdir "%CLIENTDIR%\BepInEx\AutoModSync" >nul 2>&1
xcopy "%BEPSOURCE%\BepInEx\core\*" "%CLIENTDIR%\BepInEx\core\" /E /I /Y /Q >nul
copy /y "%CLIENTDLL%" "%CLIENTDIR%\ValheimAutoModSync.Client.dll" >nul
copy /y "%APPLYEXE%" "%CLIENTDIR%\BepInEx\AutoModSync\ValheimAutoModSync.Apply.exe" >nul
copy /y "%APPLYICO%" "%CLIENTDIR%\BepInEx\AutoModSync\ValheimAutoModSync.Apply.ico" >nul
copy /y "%SERVERDLL%" "%SERVERDIR%\ValheimAutoModSync.Server.dll" >nul
if errorlevel 1 goto :Fail

if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Client" >nul 2>&1
mkdir "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Server" >nul 2>&1
mkdir "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Tools" >nul 2>&1
mkdir "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Bundled" >nul 2>&1
mkdir "%DIST%\ValheimAutoModSync-%AMS_VERSION%\THIRD_PARTY_LICENSES" >nul 2>&1
copy /y "%ROOT%README.md" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\README.md" >nul
copy /y "%ROOT%VERSION" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\VERSION" >nul
copy /y "%ROOT%LICENSE" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\LICENSE" >nul
copy /y "%ROOT%THIRD-PARTY-NOTICES.md" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\THIRD-PARTY-NOTICES.md" >nul
copy /y "%ROOT%install.bat" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\install.bat" >nul
copy /y "%INSTALLEREXE%" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\ValheimAutoModSyncInstaller.exe" >nul
copy /y "%APPLYICO%" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\ValheimAutoModSyncInstaller.ico" >nul
if errorlevel 1 goto :Fail
copy /y "%BEPZIP%" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Bundled\BepInExPack_Valheim-%BEPINEX_VERSION%.zip" >nul
if errorlevel 1 goto :Fail
copy /y "%CLIENTDIR%\ValheimAutoModSync.Client.dll" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Client\ValheimAutoModSync.Client.dll" >nul
copy /y "%CLIENTDIR%\winhttp.dll" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Client\winhttp.dll" >nul
copy /y "%CLIENTDIR%\doorstop_config.ini" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Client\doorstop_config.ini" >nul
xcopy "%CLIENTDIR%\BepInEx\*" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Client\BepInEx\" /E /I /Y /Q >nul
copy /y "%SERVERDIR%\ValheimAutoModSync.Server.dll" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Server\ValheimAutoModSync.Server.dll" >nul
copy /y "%SERVERDIR%\server-config-example.cfg" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Server\server-config-example.cfg" >nul
copy /y "%TOOLSDIR%\AutoModSync.BuildTool.exe" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\Tools\AutoModSync.BuildTool.exe" >nul
xcopy "%ROOT%THIRD_PARTY_LICENSES\*" "%DIST%\ValheimAutoModSync-%AMS_VERSION%\THIRD_PARTY_LICENSES\" /E /I /Y /Q >nul

set "AMS_ZIP_SOURCE=%DIST%\ValheimAutoModSync-%AMS_VERSION%"
set "AMS_ZIP_DEST=%DIST%\ValheimAutoModSync-%AMS_VERSION%.zip"
powershell.exe -NoLogo -NoProfile -Command "[void][Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem'); [IO.Compression.ZipFile]::CreateFromDirectory($env:AMS_ZIP_SOURCE,$env:AMS_ZIP_DEST,[IO.Compression.CompressionLevel]::Optimal,$false)"
set "AMS_ZIP_SOURCE="
set "AMS_ZIP_DEST="
if errorlevel 1 goto :Fail

echo.
echo ============================================================
echo   BUILD COMPLETE
echo ============================================================
echo Release ZIP:
echo   "%DIST%\ValheimAutoModSync-%AMS_VERSION%.zip"
echo.
echo Authenticode status: %SIGNING_STATUS%
echo This build contains no packed AutoModSync version.dll.
rmdir /s /q "%WORK%" >nul 2>&1
if not defined AMS_NO_PAUSE pause
exit /b 0

:SignReleaseBinaries
if not exist "%ROOT%sign-release.ps1" (
  if /i "%AMS_REQUIRE_SIGNING%"=="1" (
    echo ERROR: sign-release.ps1 is missing but a signed release was required.
    exit /b 1
  )
  echo WARNING: sign-release.ps1 is missing. AutoModSync binaries will be unsigned.
  exit /b 0
)

set "HAS_SIGNING_CONFIG="
if defined AMS_ARTIFACT_SIGNING_DLIB set "HAS_SIGNING_CONFIG=1"
if defined AMS_SIGN_PFX set "HAS_SIGNING_CONFIG=1"
if defined AMS_SIGN_THUMBPRINT set "HAS_SIGNING_CONFIG=1"

if not defined HAS_SIGNING_CONFIG (
  if /i "%AMS_REQUIRE_SIGNING%"=="1" (
    echo ERROR: signed release required, but no signing identity is configured.
    echo See SIGNING.md.
    exit /b 1
  )
  echo WARNING: no signing identity configured. AutoModSync binaries will be unsigned.
  exit /b 0
)

echo Signing AutoModSync-authored binaries...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "& '%ROOT%sign-release.ps1' -Files @('%BUILDTOOL%','%CLIENTDLL%','%SERVERDLL%','%APPLYEXE%','%INSTALLEREXE%')"
if errorlevel 1 exit /b 1
set "SIGNING_STATUS=SIGNED AND VERIFIED"
exit /b 0

:PreparePinnedBepInEx
set "BEPZIP=%WORK%\BepInExPack_Valheim-%BEPINEX_VERSION%.zip"
set "BEPEXTRACT=%WORK%\BepInExExtract"
echo Downloading pinned BepInEx package...
curl.exe -L --fail --retry 3 --retry-delay 2 -o "%BEPZIP%" "%BEPINEX_URL%"
if errorlevel 1 exit /b 1
set "ACTUALSHA="
"%BUILDTOOL%" sha256 "%BEPZIP%" >"%WORK%\bep-sha.txt" 2>nul
if errorlevel 1 (
  echo ERROR: Could not calculate BepInEx SHA-256 with AutoModSync BuildTool.
  exit /b 1
)
set /p ACTUALSHA=<"%WORK%\bep-sha.txt"
if not defined ACTUALSHA (
  echo ERROR: AutoModSync BuildTool returned an empty BepInEx SHA-256.
  exit /b 1
)
if /i not "%ACTUALSHA%"=="%BEPINEX_SHA256%" (
  echo ERROR: BepInEx SHA-256 verification failed.
  exit /b 1
)
mkdir "%BEPEXTRACT%" >nul 2>&1
where tar.exe >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoLogo -NoProfile -Command "Expand-Archive -LiteralPath '%BEPZIP%' -DestinationPath '%BEPEXTRACT%' -Force"
) else (
  tar.exe -xf "%BEPZIP%" -C "%BEPEXTRACT%"
)
if errorlevel 1 exit /b 1
set "BEPSOURCE=%BEPEXTRACT%\BepInExPack_Valheim"
if not exist "%BEPSOURCE%\BepInEx\core\BepInEx.dll" exit /b 1
echo BepInEx SHA-256 verified.
exit /b 0

:MissingVersion
echo ERROR: VERSION is missing or empty.
goto :FailNoWork

:BadClient
echo ERROR: A valid Valheim or Valheim Dedicated Server installation was not found.
goto :Fail

:NoCompiler
echo ERROR: Windows .NET Framework C# compiler was not found.
goto :FailNoWork

:Fail
echo.
echo Build failed.
if defined WORK if exist "%WORK%" rmdir /s /q "%WORK%" >nul 2>&1
:FailNoWork
if not defined AMS_NO_PAUSE pause
exit /b 1
