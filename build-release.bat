@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
title Valheim AutoModSync Release Builder

set "AMS_VERSION=2.5.0"
set "ROOT=%~dp0"
set "SOURCE=%ROOT%Source"
set "CLIENTDIR=%ROOT%Client"
set "SERVERDIR=%ROOT%Server"
set "TOOLSDIR=%ROOT%Tools"
set "DIST=%ROOT%Dist"
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

set "VALHEIMROOT="
if exist "%ProgramFiles(x86)%\Steam\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%ProgramFiles(x86)%\Steam\steamapps\common\Valheim"
if not defined VALHEIMROOT if exist "%ProgramFiles%\Steam\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%ProgramFiles%\Steam\steamapps\common\Valheim"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined VALHEIMROOT if exist "%%D:\SteamLibrary\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%%D:\SteamLibrary\steamapps\common\Valheim"
if not defined VALHEIMROOT (
  echo Enter the folder containing valheim.exe.
  set /p "VALHEIMROOT=Valheim path: "
)
set "VALHEIMROOT=%VALHEIMROOT:"=%"
if not exist "%VALHEIMROOT%\valheim.exe" goto :BadClient
if not exist "%VALHEIMROOT%\valheim_Data\Managed\assembly_valheim.dll" goto :BadClient

echo Valheim path: "%VALHEIMROOT%"
call :PreparePinnedBepInEx
if errorlevel 1 goto :Fail

set "BEPINEX_DLL=%BEPSOURCE%\BepInEx\core\BepInEx.dll"
set "HARMONY_DLL=%BEPSOURCE%\BepInEx\core\0Harmony.dll"
set "GAME_DLL=%VALHEIMROOT%\valheim_Data\Managed\assembly_valheim.dll"
set "ASSEMBLY_UTILS=%VALHEIMROOT%\valheim_Data\Managed\assembly_utils.dll"
set "SPLATFORM_DLL=%VALHEIMROOT%\valheim_Data\Managed\Splatform.dll"
set "STEAMWORKS_DLL=%VALHEIMROOT%\valheim_Data\Managed\com.rlabrecque.steamworks.net.dll"
set "NETSTANDARD_DLL=%VALHEIMROOT%\valheim_Data\Managed\netstandard.dll"
set "UNITY_ENGINE=%BEPSOURCE%\unstripped_corlib\UnityEngine.dll"
if not exist "%UNITY_ENGINE%" set "UNITY_ENGINE=%VALHEIMROOT%\valheim_Data\Managed\UnityEngine.dll"
set "UNITY_CORE=%BEPSOURCE%\unstripped_corlib\UnityEngine.CoreModule.dll"
if not exist "%UNITY_CORE%" set "UNITY_CORE=%VALHEIMROOT%\valheim_Data\Managed\UnityEngine.CoreModule.dll"
set "UNITY_IMGUI=%BEPSOURCE%\unstripped_corlib\UnityEngine.IMGUIModule.dll"
if not exist "%UNITY_IMGUI%" set "UNITY_IMGUI=%VALHEIMROOT%\valheim_Data\Managed\UnityEngine.IMGUIModule.dll"
set "UNITY_TEXT=%BEPSOURCE%\unstripped_corlib\UnityEngine.TextRenderingModule.dll"
if not exist "%UNITY_TEXT%" set "UNITY_TEXT=%VALHEIMROOT%\valheim_Data\Managed\UnityEngine.TextRenderingModule.dll"

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
echo Compiling AutoModSync components...
"%CSC%" @"%REFS%" /target:library /out:"%CLIENTDLL%" "%SOURCE%\ValheimAutoModSync.Client.cs"
if errorlevel 1 goto :Fail
"%CSC%" @"%REFS%" /target:library /out:"%SERVERDLL%" "%SOURCE%\ValheimAutoModSync.Server.cs"
if errorlevel 1 goto :Fail
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /out:"%APPLYEXE%" "%SOURCE%\ValheimAutoModSync.Apply.cs"
if errorlevel 1 goto :Fail

rem Sign AutoModSync-authored PE files before they are copied or packaged.
call :SignReleaseBinaries
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
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "& '%ROOT%sign-release.ps1' -Files @('%BUILDTOOL%','%CLIENTDLL%','%SERVERDLL%','%APPLYEXE%')"
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
"%BUILDTOOL%" sha256 "%BEPZIP%" >"%WORK%\sha.txt"
if errorlevel 1 exit /b 1
set /p "ACTUALSHA="<"%WORK%\sha.txt"
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

:BadClient
echo ERROR: A valid Valheim client installation was not found.
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
