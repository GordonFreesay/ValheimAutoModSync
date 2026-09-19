param(
    [switch]$SkipStandaloneBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
$ClientSource = Join-Path $Root "Source\ValheimAutoModSync.Client.cs"
$match = [regex]::Match([System.IO.File]::ReadAllText($ClientSource), 'PluginVersion\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw "Could not read PluginVersion from client source." }
$Version = $match.Groups[1].Value

if (-not $SkipStandaloneBuild) {
    Write-Host ""
    Write-Host "Building standalone release from unified source..." -ForegroundColor Cyan
    $old = $env:AMS_NO_PAUSE
    try {
        $env:AMS_NO_PAUSE = "1"
        & cmd.exe /d /c "`"$Root\build-release.bat`""
        if ($LASTEXITCODE -ne 0) { throw "build-release.bat failed with exit code $LASTEXITCODE" }
    }
    finally {
        $env:AMS_NO_PAUSE = $old
    }
}

$ClientDll = Join-Path $Root "Client\ValheimAutoModSync.Client.dll"
$ServerDll = Join-Path $Root "Server\ValheimAutoModSync.Server.dll"
$ApplyExe = Join-Path $Root "Client\BepInEx\AutoModSync\ValheimAutoModSync.Apply.exe"
$TsRoot = Join-Path $Root "Thunderstore"
$Dist = Join-Path $Root "Dist"
$Package = Join-Path $Dist ("Thunderstore-" + $Version)
$Plugins = Join-Path $Package "plugins"

foreach ($p in @(
    $ClientDll,$ServerDll,$ApplyExe,
    (Join-Path $TsRoot "icon.png"),
    (Join-Path $TsRoot "README.md"),
    (Join-Path $TsRoot "CHANGELOG.md"),
    (Join-Path $TsRoot "THIRD-PARTY-NOTICES.md"),
    (Join-Path $Root "LICENSE")
)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required package input missing: $p" }
}

if (Test-Path $Package) { Remove-Item -LiteralPath $Package -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Plugins | Out-Null

Copy-Item $ClientDll (Join-Path $Plugins "ValheimAutoModSync.Client.dll")
Copy-Item $ServerDll (Join-Path $Plugins "ValheimAutoModSync.Server.dll")
Copy-Item $ApplyExe (Join-Path $Plugins "ValheimAutoModSync.Apply.exe")
Copy-Item (Join-Path $TsRoot "icon.png") (Join-Path $Package "icon.png")
Copy-Item (Join-Path $TsRoot "README.md") (Join-Path $Package "README.md")
Copy-Item (Join-Path $TsRoot "CHANGELOG.md") (Join-Path $Package "CHANGELOG.md")
Copy-Item (Join-Path $TsRoot "THIRD-PARTY-NOTICES.md") (Join-Path $Package "THIRD-PARTY-NOTICES.md")
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $Package "LICENSE")

$manifest = [ordered]@{
    name = "ValheimAutoModSync"
    version_number = $Version
    website_url = "https://github.com/GordonFreesay/ValheimAutoModSync"
    description = "Server-driven BepInEx plugin sync for Valheim with signed manifests, SHA-256 verification, delta transfers, restart/reconnect, and no extra sync port."
    dependencies = @("denikson-BepInExPack_Valheim-5.4.2350")
} | ConvertTo-Json -Depth 4

$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $Package "manifest.json"), $manifest + [Environment]::NewLine, $enc)

$Zip = Join-Path $Dist ("GordonFreesay-ValheimAutoModSync-" + $Version + "-Thunderstore.zip")
if (Test-Path $Zip) { Remove-Item -LiteralPath $Zip -Force }
Compress-Archive -Path (Join-Path $Package "*") -DestinationPath $Zip -Force

$standalone = Join-Path $Dist ("ValheimAutoModSync-" + $Version + ".zip")
Write-Host ""
Write-Host "============================================================" -ForegroundColor DarkYellow
Write-Host "  UNIFIED RELEASE COMPLETE" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor DarkYellow
Write-Host "Standalone/manual package:"
Write-Host "  $standalone"
Write-Host "Thunderstore/r2modman package:"
Write-Host "  $Zip"
Write-Host ""
Write-Host "Both packages were built from the same compiled AutoModSync binaries."
Write-Host ""
Write-Host "Thunderstore SHA-256:"
Write-Host "  $((Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash.ToLowerInvariant())"
if (Test-Path $standalone) {
    Write-Host "Standalone SHA-256:"
    Write-Host "  $((Get-FileHash -Algorithm SHA256 -LiteralPath $standalone).Hash.ToLowerInvariant())"
}