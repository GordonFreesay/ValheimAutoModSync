param([switch]$SkipStandaloneBuild)
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "build-modsite-package.ps1") -Target Nexus -SkipStandaloneBuild:$SkipStandaloneBuild
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
