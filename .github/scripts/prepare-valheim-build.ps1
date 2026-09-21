$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$steamCmdDir = Join-Path $env:RUNNER_TEMP "steamcmd"
$steamCmdZip = Join-Path $env:RUNNER_TEMP "steamcmd.zip"
$valheimDir = Join-Path $env:RUNNER_TEMP "valheim-dedicated-server"

New-Item -ItemType Directory -Force -Path $steamCmdDir,$valheimDir | Out-Null
Invoke-WebRequest -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile $steamCmdZip
Expand-Archive -LiteralPath $steamCmdZip -DestinationPath $steamCmdDir -Force

$steamCmd = Join-Path $steamCmdDir "steamcmd.exe"
& $steamCmd +quit
Write-Host "SteamCMD bootstrap exit code: $LASTEXITCODE (self-update may return a nonzero code)."
Start-Sleep -Seconds 2

$gameAssembly = $null
for ($attempt = 1; $attempt -le 5; $attempt++) {
    Write-Host "Valheim Dedicated Server install attempt $attempt of 5..."
    & $steamCmd +@sSteamCmdForcePlatformType windows +force_install_dir $valheimDir +login anonymous +app_info_update 1 +app_update 896660 validate +quit
    $steamExit = $LASTEXITCODE
    $gameAssembly = Get-ChildItem -LiteralPath $valheimDir -Filter "assembly_valheim.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($steamExit -eq 0 -and $gameAssembly) { break }
    Write-Warning "SteamCMD attempt $attempt exited $steamExit without usable Valheim references."
    Start-Sleep -Seconds 5
}

if (-not $gameAssembly) { throw "Valheim Dedicated Server did not provide assembly_valheim.dll after 5 attempts." }
if (-not (Test-Path (Join-Path $valheimDir "valheim_server.exe"))) { throw "valheim_server.exe was not installed." }
if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) { throw "GITHUB_ENV is not available." }

"AMS_VALHEIMROOT=$valheimDir" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
Write-Host "Using managed references from $($gameAssembly.DirectoryName)"
