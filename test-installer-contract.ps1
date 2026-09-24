param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'Source\ValheimAutoModSync.Installer.cs'
$identity = Join-Path $root 'Source\AutoModSync.IdentityDisplay.cs'
$pathSafety = Join-Path $root 'Source\AutoModSync.PathSafety.cs'
$ownership = Join-Path $root 'Source\AutoModSync.OwnershipState.cs'
$manifest = Join-Path $root 'Source\AutoModSyncInstaller.manifest'
$logo = Join-Path $root 'Thunderstore\icon.png'
$temp = Join-Path $env:TEMP ('AMS26-Installer-' + $PID)
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Assert-Contains([string]$Path,[string]$Needle,[string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if ($text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) {
        throw ($Label + ' missing required contract: ' + $Needle)
    }
}

try {
    Write-Host '[1/4] Checking installer install/uninstall safety contract...'
    Assert-Contains $source 'private void UninstallClicked' 'Installer'
    Assert-Contains $source 'SelectedRoleComplete' 'Installer'
    Assert-Contains $source '_uninstall.Visible = complete' 'Installer'
    Assert-Contains $source 'RetireOwnedClientFiles(root)' 'Installer'
    Assert-Contains $source 'AutoModSyncOwnershipState.ReadFile' 'Installer'
    Assert-Contains $source 'Preserved server config and signing identity.' 'Installer'
    Assert-Contains $source 'BepInEx and unrelated mods were preserved.' 'Installer'
    Assert-Contains $source 'pending/recovery apply transaction' 'Installer'
    Write-Host '  PASS'

    Write-Host '[2/4] Checking branded/privacy presentation contract...'
    Assert-Contains $source 'ValheimAutoModSync.Branding.Logo.png' 'Installer'
    Assert-Contains $source 'AUTOMODSYNC' 'Installer'
    Assert-Contains $source 'VERIFIED MOD SYNCHRONIZATION' 'Installer'
    Assert-Contains $source 'TryApplyWindowIcon()' 'Installer window icon'
    Assert-Contains $source 'Icon.ExtractAssociatedIcon(Application.ExecutablePath)' 'Installer window icon'
    Assert-Contains $source 'AutoModSyncIdentityDisplay.VerificationCode' 'Installer'
    if ([IO.File]::ReadAllText($source).IndexOf('AppendLog("Server fingerprint:',[StringComparison]::Ordinal) -ge 0) {
        throw 'Installer must not print the full server fingerprint in normal UI.'
    }
    Write-Host '  PASS'

    Write-Host '[3/4] Compiling standalone installer with the production shared safety helpers...'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw '.NET Framework C# compiler was not found.' }

    $exe = Join-Path $temp 'ValheimAutoModSyncInstaller.exe'
    & $csc /nologo /target:winexe /optimize+ /langversion:5 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /resource:"$logo,ValheimAutoModSync.Branding.Logo.png" /win32manifest:$manifest /out:$exe $source $identity $pathSafety $ownership
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or (Get-Item -LiteralPath $exe).Length -lt 32768) {
        throw 'Compiled installer artifact is missing or unexpectedly small.'
    }
    Write-Host '  PASS'

    Write-Host '[4/4] Scanning compiled installer for first-party PII...'
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'verify-no-pii.ps1') -Root $root -ArtifactPaths $exe
    if ($LASTEXITCODE -ne 0) { throw 'Compiled installer failed the first-party PII guard.' }
    Write-Host '  PASS'

    Write-Host 'PASS: standalone installer branding, detection, uninstall safety, compilation, and PII contracts are valid.'
}
finally {
    try { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } } catch {}
}
