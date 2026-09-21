$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
. (Join-Path $Root "build-package-utils.ps1")
$Version = Get-AutoModSyncVersion -Root $Root

Write-Host ""
Write-Host "Building canonical standalone package for AutoModSync $Version..." -ForegroundColor Cyan
Invoke-AutoModSyncStandaloneBuild -Root $Root

& (Join-Path $Root "build-thunderstore.ps1") -SkipStandaloneBuild
if ($LASTEXITCODE -ne 0) { throw "Thunderstore package build failed with exit code $LASTEXITCODE" }

& (Join-Path $Root "build-nexus.ps1") -SkipStandaloneBuild
if ($LASTEXITCODE -ne 0) { throw "Nexus package build failed with exit code $LASTEXITCODE" }

& (Join-Path $Root "build-curseforge.ps1") -SkipStandaloneBuild
if ($LASTEXITCODE -ne 0) { throw "CurseForge package build failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "All distribution packages built from the same source revision and version:" -ForegroundColor Yellow
Get-ChildItem -LiteralPath (Join-Path $Root "Dist") -File |
    Where-Object { $_.Name -match [regex]::Escape($Version) -and $_.Extension -eq ".zip" } |
    Sort-Object Name |
    ForEach-Object {
        $sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host ("  {0}" -f $_.Name)
        Write-Host ("    sha256:{0}" -f $sha)
    }
