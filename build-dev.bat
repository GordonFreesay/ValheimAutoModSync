@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"

rem AutoModSync 2.6 development-only compiler.
rem Intent: produce only the three runtime binaries required for local validation.
rem Safety: this script does NOT create an installer, release ZIP, store package, Git tag, or publication artifact.

set "ROOT=%~dp0"
set "SOURCE=%ROOT%Source"
set "OUT=%ROOT%DevBuild"
set "BRANDING_PNG=%ROOT%Thunderstore\icon.png"
set "BRANDING_SCRIPT=%ROOT%build-branding-assets.ps1"
set "CSC="

if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
  echo ERROR: .NET Framework C# compiler was not found.
  exit /b 1
)

if exist "%ROOT%verify-source-docs.ps1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%verify-source-docs.ps1"
  if errorlevel 1 exit /b 1
)
if exist "%ROOT%verify-version.ps1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%verify-version.ps1"
  if errorlevel 1 exit /b 1
)

set "VALHEIMROOT=%AMS_VALHEIMROOT%"
if defined VALHEIMROOT set "VALHEIMROOT=%VALHEIMROOT:"=%"
if not defined VALHEIMROOT if exist "%ProgramFiles(x86)%\Steam\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%ProgramFiles(x86)%\Steam\steamapps\common\Valheim"
if not defined VALHEIMROOT if exist "%ProgramFiles%\Steam\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%ProgramFiles%\Steam\steamapps\common\Valheim"
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined VALHEIMROOT if exist "%%D:\SteamLibrary\steamapps\common\Valheim\valheim.exe" set "VALHEIMROOT=%%D:\SteamLibrary\steamapps\common\Valheim"

if not defined VALHEIMROOT (
  echo Enter the Valheim installation folder used for development references.
  set /p "VALHEIMROOT=Valheim path: "
)
set "VALHEIMROOT=%VALHEIMROOT:"=%"
if not exist "%VALHEIMROOT%\valheim.exe" (
  echo ERROR: valheim.exe was not found at "%VALHEIMROOT%".
  exit /b 1
)

set "BEPROOT=%VALHEIMROOT%\BepInEx"
if not exist "%BEPROOT%\core\BepInEx.dll" (
  echo ERROR: This development build expects a working BepInEx client install at "%BEPROOT%".
  echo Install/run the current AutoModSync/BepInEx client first, then rerun this script.
  exit /b 1
)

set "MANAGED=%VALHEIMROOT%\valheim_Data\Managed"
set "BEPINEX_DLL=%BEPROOT%\core\BepInEx.dll"
set "HARMONY_DLL=%BEPROOT%\core\0Harmony.dll"
set "GAME_DLL=%MANAGED%\assembly_valheim.dll"
set "ASSEMBLY_UTILS=%MANAGED%\assembly_utils.dll"
set "SPLATFORM_DLL=%MANAGED%\Splatform.dll"
set "STEAMWORKS_DLL=%MANAGED%\com.rlabrecque.steamworks.net.dll"
set "NETSTANDARD_DLL=%MANAGED%\netstandard.dll"
set "UNITY_ENGINE=%BEPROOT%\unstripped_corlib\UnityEngine.dll"
if not exist "%UNITY_ENGINE%" set "UNITY_ENGINE=%MANAGED%\UnityEngine.dll"
set "UNITY_CORE=%BEPROOT%\unstripped_corlib\UnityEngine.CoreModule.dll"
if not exist "%UNITY_CORE%" set "UNITY_CORE=%MANAGED%\UnityEngine.CoreModule.dll"
set "UNITY_IMGUI=%BEPROOT%\unstripped_corlib\UnityEngine.IMGUIModule.dll"
if not exist "%UNITY_IMGUI%" set "UNITY_IMGUI=%MANAGED%\UnityEngine.IMGUIModule.dll"
set "UNITY_TEXT=%BEPROOT%\unstripped_corlib\UnityEngine.TextRenderingModule.dll"
if not exist "%UNITY_TEXT%" set "UNITY_TEXT=%MANAGED%\UnityEngine.TextRenderingModule.dll"

if not exist "%GAME_DLL%" (
  echo ERROR: Valheim managed references were not found under "%MANAGED%".
  exit /b 1
)
if not exist "%HARMONY_DLL%" (
  echo ERROR: 0Harmony.dll was not found under "%BEPROOT%\core".
  exit /b 1
)
if not exist "%BRANDING_PNG%" (
  echo ERROR: AutoModSync branding PNG was not found at "%BRANDING_PNG%".
  exit /b 1
)
if not exist "%BRANDING_SCRIPT%" (
  echo ERROR: build-branding-assets.ps1 is missing.
  exit /b 1
)

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
set "APPLYICO=%OUT%\ValheimAutoModSync.Apply.ico"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%BRANDING_SCRIPT%" -SourcePng "%BRANDING_PNG%" -OutputIco "%APPLYICO%"
if errorlevel 1 exit /b 1
set "REFS=%OUT%\refs.rsp"
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

echo.
echo Compiling AutoModSync 2.6 development runtime only...
rem AMS_DEV_TESTS enables only local development validation hooks: client transfer interruption,
rem client/server pre-resume AMS4 compatibility emulation, and Apply transaction boundary tests.
rem The release builder never defines this symbol, so public binaries do not contain these test-only paths.
"%CSC%" @"%REFS%" /target:library /define:AMS_DEV_TESTS /resource:"%BRANDING_PNG%",ValheimAutoModSync.Branding.Logo.png /out:"%OUT%\ValheimAutoModSync.Client.dll" "%SOURCE%\ValheimAutoModSync.Client.cs" "%SOURCE%\AutoModSync.SyncUiState.cs" "%SOURCE%\AutoModSync.PathSafety.cs" "%SOURCE%\AutoModSync.ClientResourceSafety.cs" "%SOURCE%\AutoModSync.ResumeState.cs" "%SOURCE%\AutoModSync.OwnershipState.cs"
if errorlevel 1 exit /b 1

"%CSC%" @"%REFS%" /target:library /define:AMS_DEV_TESTS /out:"%OUT%\ValheimAutoModSync.Server.dll" "%SOURCE%\ValheimAutoModSync.Server.cs" "%SOURCE%\AutoModSync.PathSafety.cs" "%SOURCE%\AutoModSync.ManifestScanner.cs" "%SOURCE%\AutoModSync.ServerResourceSafety.cs" "%SOURCE%\AutoModSync.ResumeState.cs" "%SOURCE%\AutoModSync.TransferScheduler.cs" "%SOURCE%\AutoModSync.ClientPayload.cs"
if errorlevel 1 exit /b 1

rem Apply uses the same AMS_DEV_TESTS symbol for its deterministic transactional interruption markers.
"%CSC%" /nologo /target:winexe /optimize+ /langversion:5 /define:AMS_DEV_TESTS /win32icon:"%APPLYICO%" /out:"%OUT%\ValheimAutoModSync.Apply.exe" "%SOURCE%\ValheimAutoModSync.Apply.cs" "%SOURCE%\AutoModSync.PathSafety.cs" "%SOURCE%\AutoModSync.OwnershipState.cs"
if errorlevel 1 exit /b 1

del /q "%REFS%" >nul 2>&1

echo.
echo SUCCESS: development runtime built in:
echo   %OUT%
echo.
echo IMPORTANT: build-dev.bat only compiles DevBuild; it does NOT install these binaries into Valheim.
echo For live validation, close Valheim and stop the dedicated server, then run deploy-dev.ps1 with the client/server BepInEx roots.
echo.
echo No installer, release ZIP, store package, tag, or publication artifact was created.
exit /b 0
