param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'Source\ValheimAutoModSync.Installer.cs'
$engine = Join-Path $root 'Source\AutoModSync.ApplyEngine.cs'
$identity = Join-Path $root 'Source\AutoModSync.IdentityDisplay.cs'
$pathSafety = Join-Path $root 'Source\AutoModSync.PathSafety.cs'
$ownership = Join-Path $root 'Source\AutoModSync.OwnershipState.cs'
$privateKeySecurity = Join-Path $root 'Source\AutoModSync.PrivateKeySecurity.cs'
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

function Assert-NotContains([string]$Path,[string]$Needle,[string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if ($text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) {
        throw ($Label + ' contains forbidden contract: ' + $Needle)
    }
}

try {
    Write-Host '[1/6] Checking installer install/uninstall safety contract...'
    Assert-Contains $source 'private void UninstallClicked' 'Installer'
    Assert-Contains $source 'SelectedRoleComplete' 'Installer'
    Assert-Contains $source '_uninstall.Visible = complete' 'Installer'
    Assert-Contains $source 'RetireOwnedClientFiles(root)' 'Installer'
    Assert-Contains $source 'AutoModSyncOwnershipState.ReadFile' 'Installer'
    Assert-Contains $source 'Preserved server config and signing identity.' 'Installer'
    Assert-Contains $source 'BepInEx and unrelated mods were preserved.' 'Installer'
    Assert-Contains $source 'pending/recovery apply transaction' 'Installer'
    Write-Host '  PASS'

    Write-Host '[2/6] Checking branded/privacy presentation contract...'
    Assert-Contains $source 'ValheimAutoModSync.Branding.Logo.png' 'Installer'
    Assert-Contains $source 'AUTOMODSYNC' 'Installer'
    Assert-Contains $source 'VERIFIED MOD SYNCHRONIZATION' 'Installer'
    Assert-Contains $source 'TryApplyWindowIcon()' 'Installer window icon'
    Assert-Contains $source 'role.UseMnemonic = false;' 'Installer role mnemonic handling'
    Assert-Contains $source 'roleHelp.UseMnemonic = false;' 'Installer role help mnemonic handling'
    Assert-Contains $source 'Icon.ExtractAssociatedIcon(Application.ExecutablePath)' 'Installer window icon'
    Assert-Contains $source 'AutoModSyncIdentityDisplay.VerificationCode' 'Installer'
    if ([IO.File]::ReadAllText($source).IndexOf('AppendLog("Server fingerprint:',[StringComparison]::Ordinal) -ge 0) {
        throw 'Installer must not print the full server fingerprint in normal UI.'
    }
    Write-Host '  PASS'

    Write-Host '[3/6] Checking consolidated updater/process-safety contract...'
    Assert-Contains $source '--apply-pending' 'Installer updater mode'
    Assert-Contains $source 'ApplyProgressForm' 'Visible updater UI'
    Assert-Contains $source 'Waiting for Valheim to close normally' 'Normal-exit updater UI'
    Assert-Contains $source 'AutoModSyncApplyEngine.Run(_amsRoot)' 'Installer transaction engine'
    Assert-Contains $source 'psi.Verb = "runas";' 'Interactive installer elevation'
    Assert-Contains $source 'SERVER OPERATOR RESPONSIBILITY: AutoModSync does not grant redistribution rights for third-party mods.' 'Server operator redistribution notice'
    Assert-Contains $source 'You are responsible for ensuring every synchronized third-party file may be provided to connecting clients.' 'Server operator redistribution notice'
    Assert-Contains $manifest 'requestedExecutionLevel level="asInvoker"' 'Installer manifest'
    Assert-NotContains $manifest 'requireAdministrator' 'Installer manifest'
    Assert-NotContains $source '.Kill(' 'Installer process safety'
    Assert-NotContains $engine '.Kill(' 'Transaction-engine process safety'
    Assert-NotContains $engine 'Process.Kill' 'Transaction-engine process safety'
    Assert-NotContains $engine 'static int Main(' 'Transaction engine must not be standalone'
    Write-Host '  PASS'

    Write-Host '[4/6] Checking package/build consolidation contract...'
    $clientSource = Join-Path $root 'Source\ValheimAutoModSync.Client.cs'
    $releaseBuild = Join-Path $root 'build-release.bat'
    $devBuild = Join-Path $root 'build-dev.bat'
    $modsiteBuild = Join-Path $root 'build-modsite-package.ps1'
    Assert-Contains $clientSource 'ValheimAutoModSyncInstaller.exe' 'Client updater routing'
    Assert-NotContains $clientSource 'ProcessWindowStyle.Hidden' 'Client updater visibility'
    Assert-Contains $releaseBuild 'AutoModSync.ApplyEngine.cs' 'Release installer compilation'
    Assert-Contains $devBuild 'AutoModSync.ApplyEngine.cs' 'Development installer compilation'
    Assert-Contains $modsiteBuild 'ValheimAutoModSyncInstaller.exe' 'Nexus/CurseForge package'
    Assert-NotContains $releaseBuild 'ValheimAutoModSync.Apply.exe' 'Release build'
    Assert-NotContains $devBuild '/out:"%OUT%\ValheimAutoModSync.Apply.exe"' 'Development build'
    Assert-NotContains $devBuild 'ValheimAutoModSync.Apply.cs' 'Development build'
    Assert-Contains $devBuild 'Legacy ValheimAutoModSync.Apply.exe unexpectedly exists in DevBuild.' 'Development stale-output guard'
    Assert-NotContains $modsiteBuild 'ValheimAutoModSync.Apply.exe' 'Nexus/CurseForge package'
    Write-Host '  PASS'

    Write-Host '[5/6] Compiling standalone installer with the production shared safety helpers...'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw '.NET Framework C# compiler was not found.' }

    $exe = Join-Path $temp 'ValheimAutoModSyncInstaller.exe'
    & $csc /nologo /target:winexe /optimize+ /langversion:5 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /resource:"$logo,ValheimAutoModSync.Branding.Logo.png" /win32manifest:$manifest /out:$exe $source $engine $identity $privateKeySecurity $pathSafety $ownership
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or (Get-Item -LiteralPath $exe).Length -lt 32768) {
        throw 'Compiled installer artifact is missing or unexpectedly small.'
    }
    Write-Host '  PASS'

    Write-Host '[6/6] Scanning compiled installer for first-party PII...'
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'verify-no-pii.ps1') -Root $root -ArtifactPaths $exe
    if ($LASTEXITCODE -ne 0) { throw 'Compiled installer failed the first-party PII guard.' }
    Write-Host '  PASS'

    Write-Host 'PASS: single installer/updater branding, process safety, packaging, compilation, uninstall safety, and PII contracts are valid.'
}
finally {
    try { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } } catch {}
}
