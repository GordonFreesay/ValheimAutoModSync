@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
title AutoModSync Release Builder

echo.
echo ============================================================
echo   AutoModSync 2.4.4 - Release Builder
echo ============================================================
echo.

set "ROOT=%~dp0"
set "SOURCE=%ROOT%Source"
set "TEMPLATE=%ROOT%Client\\version.template.dll"
set "OUTPUT=%ROOT%Client\\version.dll"
set "CLIENTOUTPUT=%ROOT%Client\\ValheimAutoModSync.Client.dll"
set "SERVEROUTPUT=%ROOT%Server\\ValheimAutoModSync.Server.dll"
set "TOOLOUTPUT=%ROOT%Tools\\AutoModSync.BuildTool.exe"
set "DETECTPS=%ROOT%Detect-Valheim.ps1"
set "BEPINEX_VERSION=5.4.2350"
set "BEPINEX_URL=https://gcdn.thunderstore.io/live/repository/packages/denikson-BepInExPack_Valheim-5.4.2350.zip"
set "BEPINEX_SHA256=37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f"

set "CSC="
if exist "%WINDIR%\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe" set "CSC=%WINDIR%\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe"
if not defined CSC if exist "%WINDIR%\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe" set "CSC=%WINDIR%\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe"
if not defined CSC goto :NoCompiler

set "WORK=%TEMP%\\ValheimAutoModSync_Build_%RANDOM%_%RANDOM%"
mkdir "%WORK%" >nul 2>&1
set "BUILDTOOL=%WORK%\\AutoModSync.BuildTool.exe"
"%CSC%" /nologo /target:exe /optimize+ /langversion:5 /out:"%BUILDTOOL%" "%SOURCE%\\AutoModSync.BuildTool.cs"
if errorlevel 1 goto :Fail
if not exist "%ROOT%Tools" mkdir "%ROOT%Tools" >nul 2>&1
copy /y "%BUILDTOOL%" "%TOOLOUTPUT%" >nul
if errorlevel 1 goto :Fail

set "DETECTEDROOT="
echo Detecting normal Valheim installation...
if exist "%DETECTPS%" (
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%DETECTPS%" >"%WORK%\\client.txt" 2>nul
    if exist "%WORK%\\client.txt" set /p "DETECTEDROOT="<"%WORK%\\client.txt"
)
echo.
if defined DETECTEDROOT goto :HaveDetectedClient
set "VALHEIMROOT="
set /p "VALHEIMROOT=Enter the folder containing valheim.exe: "
goto :ValidateClient

:HaveDetectedClient
echo Valheim path: "%DETECTEDROOT%"
echo If correct, hit Enter.
echo Do NOT use quotation marks if entering a different path.
echo.
set "VALHEIMROOT="
set /p "VALHEIMROOT=If not, enter Valheim client path: "
if not defined VALHEIMROOT set "VALHEIMROOT=%DETECTEDROOT%"

:ValidateClient
set "VALHEIMROOT=%VALHEIMROOT:"=%"
if not defined VALHEIMROOT goto :BadClient
pushd "%VALHEIMROOT%" >nul 2>&1
if errorlevel 1 goto :BadClient
set "VALHEIMROOT=%CD%"
popd
if not exist "%VALHEIMROOT%\\valheim.exe" goto :BadClient
if not exist "%VALHEIMROOT%\\valheim_Data\\Managed\\assembly_valheim.dll" goto :BadClient

echo.
echo Verified Valheim path:
echo   "%VALHEIMROOT%"
echo.
call :PreparePinnedBepInEx
if errorlevel 1 goto :Fail

set "BEPINEX_DLL=%BEPSOURCE%\\BepInEx\\core\\BepInEx.dll"
set "HARMONY_DLL=%BEPSOURCE%\\BepInEx\\core\\0Harmony.dll"
set "GAME_DLL=%VALHEIMROOT%\\valheim_Data\\Managed\\assembly_valheim.dll"
set "ASSEMBLY_UTILS=%VALHEIMROOT%\\valheim_Data\\Managed\\assembly_utils.dll"
set "SPLATFORM_DLL=%VALHEIMROOT%\\valheim_Data\\Managed\\Splatform.dll"
set "STEAMWORKS_DLL=%VALHEIMROOT%\\valheim_Data\\Managed\\com.rlabrecque.steamworks.net.dll"
set "NETSTANDARD_DLL=%VALHEIMROOT%\\valheim_Data\\Managed\\netstandard.dll"
set "UNITY_ENGINE=%BEPSOURCE%\\unstripped_corlib\\UnityEngine.dll"
if not exist "%UNITY_ENGINE%" set "UNITY_ENGINE=%VALHEIMROOT%\\valheim_Data\\Managed\\UnityEngine.dll"
set "UNITY_CORE=%BEPSOURCE%\\unstripped_corlib\\UnityEngine.CoreModule.dll"
if not exist "%UNITY_CORE%" set "UNITY_CORE=%VALHEIMROOT%\\valheim_Data\\Managed\\UnityEngine.CoreModule.dll"
set "UNITY_IMGUI=%BEPSOURCE%\\unstripped_corlib\\UnityEngine.IMGUIModule.dll"
if not exist "%UNITY_IMGUI%" set "UNITY_IMGUI=%VALHEIMROOT%\\valheim_Data\\Managed\\UnityEngine.IMGUIModule.dll"
set "UNITY_TEXT=%BEPSOURCE%\\unstripped_corlib\\UnityEngine.TextRenderingModule.dll"
if not exist "%UNITY_TEXT%" set "UNITY_TEXT=%VALHEIMROOT%\\valheim_Data\\Managed\\UnityEngine.TextRenderingModule.dll"

if not exist "%BEPINEX_DLL%" goto :MissingReference
if not exist "%HARMONY_DLL%" goto :MissingReference
if not exist "%GAME_DLL%" goto :MissingReference
if not exist "%NETSTANDARD_DLL%" goto :MissingReference
if not exist "%UNITY_ENGINE%" goto :MissingReference
if not exist "%UNITY_CORE%" goto :MissingReference
if not exist "%UNITY_IMGUI%" goto :MissingReference
if not exist "%UNITY_TEXT%" goto :MissingReference
if not exist "%SPLATFORM_DLL%" goto :MissingReference
if not exist "%STEAMWORKS_DLL%" goto :MissingReference

set "REFS=%WORK%\\refs.rsp"
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

set "CLIENTDLL=%WORK%\\ValheimAutoModSync.Client.dll"
set "SERVERDLL=%WORK%\\ValheimAutoModSync.Server.dll"
set "APPLYEXE=%WORK%\\ValheimAutoModSync.Apply.exe"
echo Compiling AutoModSync runtime components...
"%CSC%" @"%REFS%" /target:library /out:"%CLIENTDLL%" "%SOURCE%\\ValheimAutoModSync.Client.cs"
if errorlevel 1 goto :Fail
"%CSC%" @"%REFS%" /target:library /out:"%SERVERDLL%" "%SOURCE%\\ValheimAutoModSync.Server.cs"
if errorlevel 1 goto :Fail
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /out:"%APPLYEXE%" "%SOURCE%\\ValheimAutoModSync.Apply.cs"
if errorlevel 1 goto :Fail

if not exist "%ROOT%Client" mkdir "%ROOT%Client" >nul 2>&1
if not exist "%ROOT%Server" mkdir "%ROOT%Server" >nul 2>&1
copy /y "%CLIENTDLL%" "%CLIENTOUTPUT%" >nul
if errorlevel 1 goto :Fail
copy /y "%SERVERDLL%" "%SERVEROUTPUT%" >nul
if errorlevel 1 goto :Fail

echo Building universal version.dll...
"%BUILDTOOL%" pack "%TEMPLATE%" "%OUTPUT%" "%BEPSOURCE%" "%CLIENTDLL%" "%APPLYEXE%"
if errorlevel 1 goto :Fail

echo.
echo ============================================================
echo   RELEASE BUILD COMPLETE
echo ============================================================
echo.
echo Built:
echo   "%OUTPUT%"
echo   "%CLIENTOUTPUT%"
echo   "%SERVEROUTPUT%"
echo   "%TOOLOUTPUT%"
echo.
echo Copy Client\\, Server\\ and Tools\\ into the matching public staging folders.
echo.
rmdir /s /q "%WORK%" >nul 2>&1
pause
exit /b 0

:PreparePinnedBepInEx
set "BEPZIP=%WORK%\\BepInExPack_Valheim-%BEPINEX_VERSION%.zip"
set "BEPEXTRACT=%WORK%\\BepInExExtract"
set "BUNDLED=%ROOT%Bundled\\BepInExPack_Valheim-%BEPINEX_VERSION%.zip"
if exist "%BUNDLED%" goto :UseBundledBepInEx
where curl.exe >nul 2>&1
if errorlevel 1 goto :NoCurl
echo Downloading pinned BepInEx package...
curl.exe -L --fail --retry 3 --retry-delay 2 -o "%BEPZIP%" "%BEPINEX_URL%"
if errorlevel 1 exit /b 1
goto :VerifyBepInEx

:UseBundledBepInEx
copy /y "%BUNDLED%" "%BEPZIP%" >nul
if errorlevel 1 exit /b 1

:VerifyBepInEx
set "ACTUALSHA="
"%BUILDTOOL%" sha256 "%BEPZIP%" >"%WORK%\\sha.txt"
if errorlevel 1 exit /b 1
set /p "ACTUALSHA="<"%WORK%\\sha.txt"
if /i not "%ACTUALSHA%"=="%BEPINEX_SHA256%" goto :BadBepHash
mkdir "%BEPEXTRACT%" >nul 2>&1
where tar.exe >nul 2>&1
if errorlevel 1 goto :ExpandWithPowerShell
tar.exe -xf "%BEPZIP%" -C "%BEPEXTRACT%"
if errorlevel 1 exit /b 1
goto :BepExtracted

:ExpandWithPowerShell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%BEPZIP%' -DestinationPath '%BEPEXTRACT%' -Force"
if errorlevel 1 exit /b 1

:BepExtracted
set "BEPSOURCE=%BEPEXTRACT%\\BepInExPack_Valheim"
if not exist "%BEPSOURCE%\\BepInEx\\core\\BepInEx.dll" (
    echo ERROR: The pinned BepInEx package did not contain the expected files.
    exit /b 1
)
echo SHA-256 verified.
exit /b 0

:NoCurl
echo ERROR: curl.exe was not found.
exit /b 1

:BadBepHash
echo ERROR: BepInEx SHA-256 verification failed.
exit /b 1

:BadClient
echo ERROR: valheim.exe and its managed assemblies were not found in that folder.
goto :Fail

:MissingReference
echo ERROR: A required compile reference could not be found.
echo Verify the normal Valheim installation with Steam and try again.
goto :Fail

:NoCompiler
echo ERROR: Windows .NET Framework C# compiler was not found.
goto :FailNoWork

:Fail
echo.
echo Build failed.
if defined WORK if exist "%WORK%" rmdir /s /q "%WORK%" >nul 2>&1
:FailNoWork
pause
exit /b 1
