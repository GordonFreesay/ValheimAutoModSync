param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot

function Read-Text([string]$Relative) {
    $path = Join-Path $root $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $Relative" }
    return [IO.File]::ReadAllText($path)
}

function Assert-Contains([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) {
        throw "$Label is missing required release text: $Needle"
    }
}

function Assert-NotContains([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) {
        throw "$Label still contains forbidden release-prep text: $Needle"
    }
}

$version = (Read-Text 'VERSION').Trim()
if ($version -ne '2.6.0') { throw "Release-readiness gate is for 2.6.0; VERSION is $version." }

Write-Host '[1/6] Validating release-facing version/status text...'
$readme = Read-Text 'README.md'
$distribution = Read-Text 'DISTRIBUTION.md'
$changelog = Read-Text 'Thunderstore\CHANGELOG.md'
$thunderReadme = Read-Text 'Thunderstore\README.md'
$modSitesReadme = Read-Text 'ModSites\README.md'
$releaseNotes = Read-Text 'RELEASE-NOTES-2.6.0.md'
$testing = Read-Text 'TESTING-2.6.md'

Assert-Contains $readme '**Current public release: 2.6.0**' 'README'
Assert-Contains $readme 'ValheimAutoModSync-2.6.0.zip' 'README'
Assert-Contains $distribution 'every channel is **2.6.0**' 'DISTRIBUTION'
Assert-Contains $distribution 'GordonFreesay-ValheimAutoModSync-2.6.0.zip' 'DISTRIBUTION'
Assert-Contains $changelog '## 2.6.0' 'Thunderstore changelog'
Assert-NotContains $changelog '## 2.6.0 (development)' 'Thunderstore changelog'
Assert-Contains $thunderReadme '2.6.0 synchronizes' 'Thunderstore README'
Assert-Contains $modSitesReadme 'In 2.6,' 'ModSites README'
Assert-Contains $releaseNotes '# Valheim AutoModSync 2.6.0' 'Release notes'
Write-Host '  PASS'

Write-Host '[2/6] Validating version metadata...'
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'verify-version.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Version consistency validation failed.' }
Write-Host '  PASS'

Write-Host '[3/6] Validating source documentation and first-party privacy...'
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'verify-source-docs.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Source documentation validation failed.' }
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'verify-no-pii.ps1') -Root $root
if ($LASTEXITCODE -ne 0) { throw 'First-party PII validation failed.' }
Write-Host '  PASS'

Write-Host '[4/6] Validating release provenance contract...'
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-release-provenance.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Release provenance contract failed.' }
Write-Host '  PASS'

Write-Host '[5/6] Validating installer release contract...'
& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-installer-contract.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Installer contract failed.' }
Write-Host '  PASS'

Write-Host '[6/6] Validating freeze state and final pending gate...'
$unchecked = @([regex]::Matches($testing,'(?m)^- \[ \] .+$') | ForEach-Object { $_.Value })
if ($unchecked.Count -gt 1) {
    throw ('Expected at most the final tagged-artifact gate to remain unchecked; found ' + $unchecked.Count + ': ' + ($unchecked -join ' | '))
}
if ($unchecked.Count -eq 1 -and $unchecked[0].IndexOf('Real tag/release workflow creates retrievable attestations',[StringComparison]::Ordinal) -lt 0) {
    throw ('Unexpected remaining TESTING-2.6 gate: ' + $unchecked[0])
}

$releaseBuild = Read-Text 'build-release.bat'
Assert-NotContains $releaseBuild '/define:AMS_DEV_TESTS' 'Release build'
Assert-Contains $releaseBuild 'VERIFYING-RELEASES.md' 'Release build'
Assert-Contains $releaseBuild 'SIGNING.md' 'Release build'
Assert-Contains $releaseBuild 'SHA256SUMS.txt' 'Release build'
Write-Host '  PASS'

Write-Host ''
if ($unchecked.Count -eq 0) { Write-Host 'PASS: AutoModSync 2.6.0 source and tagged-artifact qualification gates are complete.' } else { Write-Host 'PASS: AutoModSync 2.6.0 source is release-frozen; only final tagged-artifact attestation verification remains.' }
