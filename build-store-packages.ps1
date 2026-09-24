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
$ChecksumScript = Join-Path $Root "write-release-checksums.ps1"
if (-not (Test-Path -LiteralPath $ChecksumScript -PathType Leaf)) {
    throw "Release checksum generator is missing: $ChecksumScript"
}
& $ChecksumScript -Root $Root
if ($LASTEXITCODE -ne 0) { throw "Release checksum generation failed with exit code $LASTEXITCODE" }

Get-Content -LiteralPath (Join-Path $Root "Dist\SHA256SUMS.txt") | ForEach-Object {
    Write-Host ("  {0}" -f $_)
}
