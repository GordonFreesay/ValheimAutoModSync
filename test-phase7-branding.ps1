param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sandbox = Join-Path $env:TEMP ('AMS26-Phase7-Branding-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$ico = Join-Path $sandbox 'ValheimAutoModSync.Apply.ico'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Contains([string]$Path, [string]$Needle, [string]$Label) {
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw ($Label + ' missing required wiring: ' + $Needle)
    }
}

try {
    Write-Host '[1/4] Generating the helper ICO from the canonical AMS package logo...'
    & (Join-Path $root 'build-branding-assets.ps1') -SourcePng (Join-Path $root 'Thunderstore\icon.png') -OutputIco $ico
    Assert-True (Test-Path -LiteralPath $ico -PathType Leaf) 'Generated ICO was not created.'
    Write-Host '  PASS'

    Write-Host '[2/4] Validating ICO container and all seven PNG-backed image frames...'
    $bytes = [System.IO.File]::ReadAllBytes($ico)
    Assert-True ($bytes.Length -gt 1024) 'Generated ICO was unexpectedly small.'
    Assert-True ($bytes[0] -eq 0 -and $bytes[1] -eq 0 -and $bytes[2] -eq 1 -and $bytes[3] -eq 0) 'ICO header was invalid.'
    $count = [System.BitConverter]::ToUInt16($bytes, 4)
    Assert-True ($count -eq 7) ('Expected 7 icon frames, found ' + $count + '.')
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + (16 * $i)
        $length = [System.BitConverter]::ToUInt32($bytes, $entry + 8)
        $offset = [System.BitConverter]::ToUInt32($bytes, $entry + 12)
        Assert-True ($length -gt 64) ('ICO frame ' + $i + ' was unexpectedly small.')
        Assert-True (($offset + $length) -le $bytes.Length) ('ICO frame ' + $i + ' exceeded the container.')
        Assert-True ($bytes[$offset] -eq 0x89 -and $bytes[$offset + 1] -eq 0x50 -and $bytes[$offset + 2] -eq 0x4E -and $bytes[$offset + 3] -eq 0x47) ('ICO frame ' + $i + ' was not PNG-backed.')
    }
    Write-Host '  PASS'

    Write-Host '[3/4] Verifying the production client is state-driven and expects the embedded logo resource...'
    $client = Join-Path $root 'Source\ValheimAutoModSync.Client.cs'
    Assert-Contains $client 'private static readonly AutoModSyncUiState _uiState' 'Client UI'
    Assert-Contains $client 'ValheimAutoModSync.Branding.Logo.png' 'Client UI'
    Assert-Contains $client 'Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")' 'Client UI'
    if ([System.IO.File]::ReadAllText($client).IndexOf('ImageConversion.LoadImage(texture, bytes)', [System.StringComparison]::Ordinal) -ge 0) {
        throw 'Client UI must late-bind Unity PNG decoding; direct ImageConversion.LoadImage overload resolution breaks the legacy compiler against current Valheim assemblies.'
    }
    Assert-Contains $client 'AutoModSyncUiPhase.Downloading' 'Client UI'
    Assert-Contains $client '_uiState.UpdateTransfer' 'Client UI'
    Assert-Contains $client '_uiState.BeginVerification' 'Client UI'
    Assert-Contains $client '_uiState.SetComparison' 'Client UI'
    Assert-Contains $client '_uiState.SetQueue' 'Client UI'
    Assert-Contains $client 'phase7-test-ui-preview.once' 'Client UI preview'
    Write-Host '  PASS'

    Write-Host '[4/4] Verifying dev/release/deploy/store packaging keeps the helper ICO beside the EXE...'
    $devBuild = Join-Path $root 'build-dev.bat'
    Assert-Contains $devBuild '/resource:"%BRANDING_PNG%",ValheimAutoModSync.Branding.Logo.png' 'Dev build'
    Assert-Contains $devBuild '/win32icon:"%APPLYICO%"' 'Dev build'
    if ([System.IO.File]::ReadAllText($devBuild).IndexOf('/reference:"%UNITY_IMAGE%"', [System.StringComparison]::Ordinal) -ge 0) {
        throw 'Dev build must not directly reference UnityEngine.ImageConversionModule.dll with the legacy compiler.'
    }
    Assert-Contains (Join-Path $root 'build-release.bat') '/resource:"%BRANDING_PNG%",ValheimAutoModSync.Branding.Logo.png' 'Release build'
    Assert-Contains (Join-Path $root 'build-release.bat') '/win32icon:"%APPLYICO%"' 'Release build'
    Assert-Contains (Join-Path $root 'build-release.bat') 'ValheimAutoModSyncInstaller.ico' 'Standalone installer package'
    Assert-Contains (Join-Path $root 'deploy-dev.ps1') "'Apply helper icon'" 'Dev deploy'
    Assert-Contains (Join-Path $root 'build-thunderstore.ps1') 'ValheimAutoModSync.Apply.ico' 'Thunderstore package'
    Assert-Contains (Join-Path $root 'build-modsite-package.ps1') 'ValheimAutoModSync.Apply.ico' 'Mod-site package'
    Assert-Contains (Join-Path $root 'install.bat') 'CLIENTAPPLY_ICON_PAYLOAD' 'Standalone installer'
    Write-Host '  PASS'

    Write-Host 'PASS: Phase 7 branded UI/build packaging contract is deterministic.'
}
finally {
    if ($KeepSandbox) {
        Write-Host ('Sandbox retained: ' + $sandbox)
    }
    else {
        try { if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force } } catch {}
    }
}
