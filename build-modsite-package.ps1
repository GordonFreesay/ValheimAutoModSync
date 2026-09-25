param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("Nexus","CurseForge")]
    [string]$Target,
    [switch]$SkipStandaloneBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
. (Join-Path $Root "build-package-utils.ps1")

$Version = Get-AutoModSyncVersion -Root $Root

if (-not $SkipStandaloneBuild) {
    Write-Host ""
    Write-Host "Building standalone release from unified source..." -ForegroundColor Cyan
    Invoke-AutoModSyncStandaloneBuild -Root $Root
}

$ClientDll = Join-Path $Root "Client\ValheimAutoModSync.Client.dll"
$ServerDll = Join-Path $Root "Server\ValheimAutoModSync.Server.dll"
$ApplyExe = Join-Path $Root "Client\BepInEx\AutoModSync\ValheimAutoModSync.Apply.exe"
$ApplyIco = Join-Path $Root "Client\BepInEx\AutoModSync\ValheimAutoModSync.Apply.ico"
$Dist = Join-Path $Root "Dist"
$Package = Join-Path $Dist ("{0}-{1}" -f $Target, $Version)
$PluginDir = Join-Path $Package "BepInEx\plugins\GordonFreesay-ValheimAutoModSync"
$DocsDir = Join-Path $Package "docs"

foreach ($p in @(
    $ClientDll,
    $ServerDll,
    $ApplyExe,
    $ApplyIco,
    (Join-Path $Root "ModSites\README.md"),
    (Join-Path $Root "LICENSE"),
    (Join-Path $Root "THIRD-PARTY-NOTICES.md"),
    (Join-Path $Root "Server\server-config-example.cfg"),
    (Join-Path $Root "VERSION")
)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required package input missing: $p" }
}

if (Test-Path -LiteralPath $Package) { Remove-Item -LiteralPath $Package -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PluginDir,$DocsDir | Out-Null

Copy-Item $ClientDll (Join-Path $PluginDir "ValheimAutoModSync.Client.dll")
Copy-Item $ServerDll (Join-Path $PluginDir "ValheimAutoModSync.Server.dll")
Copy-Item $ApplyExe (Join-Path $PluginDir "ValheimAutoModSync.Apply.exe")
Copy-Item $ApplyIco (Join-Path $PluginDir "ValheimAutoModSync.Apply.ico")
Copy-Item (Join-Path $Root "ModSites\README.md") (Join-Path $Package "README.md")
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $Package "LICENSE")
Copy-Item (Join-Path $Root "THIRD-PARTY-NOTICES.md") (Join-Path $Package "THIRD-PARTY-NOTICES.md")
Copy-Item (Join-Path $Root "VERSION") (Join-Path $Package "VERSION")
Copy-Item (Join-Path $Root "Server\server-config-example.cfg") (Join-Path $DocsDir "server-config-example.cfg")

$Zip = Join-Path $Dist ("ValheimAutoModSync-{0}-{1}.zip" -f $Version, $Target)
New-AutoModSyncZip -SourceDirectory $Package -DestinationZip $Zip

if ($Target -eq "Nexus") {
    Assert-NoNestedArchives -ZipPath $Zip
}

Write-Host ""
Write-Host ("{0} package ready:" -f $Target) -ForegroundColor Yellow
Write-Host "  $Zip"
Write-Host ("SHA-256: {0}" -f (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant())
