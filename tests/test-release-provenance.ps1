param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path $env:TEMP ('AMS26-Provenance-' + $PID)
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Assert-True([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Contains([string]$Path,[string]$Needle,[string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if ($text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) {
        throw ($Label + ' missing required contract: ' + $Needle)
    }
}

try {
    Write-Host '[1/4] Validating deterministic SHA256SUMS generation...'
    $dist = Join-Path $temp 'Dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'VERSION'),'9.9.9',[Text.UTF8Encoding]::new($false))
    $names = @(
        'ValheimAutoModSync-9.9.9.zip',
        'ValheimAutoModSync-9.9.9-Nexus.zip',
        'ValheimAutoModSync-9.9.9-CurseForge.zip',
        'GordonFreesay-ValheimAutoModSync-9.9.9.zip'
    )
    for ($i = 0; $i -lt $names.Count; $i++) {
        [IO.File]::WriteAllText((Join-Path $dist $names[$i]),('fixture-' + $i),[Text.UTF8Encoding]::new($false))
    }

    & (Join-Path $root 'write-release-checksums.ps1') -Root $temp

    $manifest = Join-Path $dist 'SHA256SUMS.txt'
    $lines = @([IO.File]::ReadAllLines($manifest))
    Assert-True ($lines.Count -eq 4) ('Expected four checksum subjects; found ' + $lines.Count + '.')
    $expectedOrder = @($names | Sort-Object)
    for ($i = 0; $i -lt $expectedOrder.Count; $i++) {
        $name = $expectedOrder[$i]
        $hash = (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ($lines[$i] -eq ($hash + ' *' + $name)) ('Checksum line mismatch for ' + $name)
    }
    Write-Host '  PASS'

    Write-Host '[2/4] Validating canonical/tag workflow provenance permissions and subjects...'
    $distribution = Join-Path $root '.github\workflows\distribution-packages.yml'
    $release = Join-Path $root '.github\workflows\release-build.yml'
    foreach ($wf in @($distribution,$release)) {
        Assert-Contains $wf 'id-token: write' (Split-Path $wf -Leaf)
        Assert-Contains $wf 'attestations: write' (Split-Path $wf -Leaf)
        Assert-Contains $wf 'uses: actions/attest@v4' (Split-Path $wf -Leaf)
    }
    Assert-Contains $distribution 'subject-checksums: Dist/SHA256SUMS.txt' 'Distribution workflow'
    Assert-Contains $distribution 'subject-path: Dist/SHA256SUMS.txt' 'Distribution workflow'
    Assert-Contains $release 'subject-path: Dist/ValheimAutoModSync-${{ env.AMS_VERSION }}.zip' 'Release workflow'
    Write-Host '  PASS'

    Write-Host '[3/4] Validating exact store-upload provenance coverage...'
    $storeContracts = @(
        @('.github\workflows\publish-nexus.yml','Dist/ValheimAutoModSync-${{ env.AMS_VERSION }}-Nexus.zip'),
        @('.github\workflows\publish-curseforge.yml','Dist/ValheimAutoModSync-${{ env.AMS_VERSION }}-CurseForge.zip'),
        @('.github\workflows\publish-thunderstore.yml','Dist/GordonFreesay-ValheimAutoModSync-${{ env.AMS_VERSION }}.zip')
    )
    foreach ($contract in $storeContracts) {
        $wf = Join-Path $root $contract[0]
        $text = [IO.File]::ReadAllText($wf)
        Assert-True ($text.IndexOf('\${{',[StringComparison]::Ordinal) -lt 0) ((Split-Path $wf -Leaf) + ' contains an escaped GitHub expression.')
        Assert-Contains $wf 'id-token: write' (Split-Path $wf -Leaf)
        Assert-Contains $wf 'attestations: write' (Split-Path $wf -Leaf)
        Assert-Contains $wf 'uses: actions/attest@v4' (Split-Path $wf -Leaf)
        Assert-Contains $wf $contract[1] (Split-Path $wf -Leaf)
    }
    Write-Host '  PASS'

    Write-Host '[4/4] Validating local-build provenance wording and checksum output...'
    $localBuild = Join-Path $root 'build-release.bat'
    Assert-Contains $localBuild 'write-release-checksums.ps1' 'Local release build'
    Assert-Contains $localBuild 'This local build is NOT GitHub-attested.' 'Local release build'
    Assert-Contains $localBuild 'GitHub/Sigstore provenance is generated only by official GitHub Actions workflows.' 'Local release build'
    Write-Host '  PASS'

    Write-Host 'PASS: release checksums and GitHub/Sigstore provenance contracts are deterministic and cover canonical plus store-specific artifacts.'
}
finally {
    try { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } } catch {}
}
