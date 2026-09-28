param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$clientPath = Join-Path $root 'Source\ValheimAutoModSync.Client.cs'
$serverPath = Join-Path $root 'Source\ValheimAutoModSync.Server.cs'
$installerPath = Join-Path $root 'Source\ValheimAutoModSync.Installer.cs'
$enginePath = Join-Path $root 'Source\AutoModSync.ApplyEngine.cs'
$manifestPath = Join-Path $root 'Source\AutoModSyncInstaller.manifest'
$legacyApplySource = Join-Path $root 'Source\ValheimAutoModSync.Apply.cs'
$releaseBuildPath = Join-Path $root 'build-release.bat'
$devBuildPath = Join-Path $root 'build-dev.bat'
$modsitePath = Join-Path $root 'build-modsite-package.ps1'
$modsiteReadmePath = Join-Path $root 'ModSites\README.md'
$thunderstorePath = Join-Path $root 'build-thunderstore.ps1'
$installBatPath = Join-Path $root 'install.bat'
$signingPath = Join-Path $root 'SIGNING.md'

function Text([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" }
    return [IO.File]::ReadAllText($Path)
}
function Require([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw "$Label missing: $Needle" }
}
function Forbid([string]$Text,[string]$Needle,[string]$Label) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw "$Label contains forbidden text: $Needle" }
}
function Section([string]$Text,[string]$Start,[string]$End,[string]$Label) {
    $a = $Text.IndexOf($Start,[StringComparison]::Ordinal)
    if ($a -lt 0) { throw "$Label start marker missing: $Start" }
    $b = $Text.IndexOf($End,$a + $Start.Length,[StringComparison]::Ordinal)
    if ($b -lt 0) { throw "$Label end marker missing: $End" }
    return $Text.Substring($a,$b-$a)
}

$client = Text $clientPath
$server = Text $serverPath
$installer = Text $installerPath
$engine = Text $enginePath
$manifest = Text $manifestPath
$releaseBuild = Text $releaseBuildPath
$devBuild = Text $devBuildPath
$modsite = Text $modsitePath
$modsiteReadme = Text $modsiteReadmePath
$thunderstore = Text $thunderstorePath
$installBat = Text $installBatPath
$signing = Text $signingPath

Write-Host '[1/8] No standalone Apply helper source or build output...'
if (Test-Path -LiteralPath $legacyApplySource) {
    throw 'Source/ValheimAutoModSync.Apply.cs still exists; 2.6.1 must not retain a standalone Apply helper implementation.'
}
foreach ($item in @(
    @($releaseBuild,'release build'),
    @($devBuild,'development build'),
    @($modsite,'Nexus/CurseForge package'),
    @($thunderstore,'package-manager compatibility builder'),
    @($signing,'signing documentation')
)) {
    Forbid $item[0] 'ValheimAutoModSync.Apply.exe' $item[1]
    Forbid $item[0] 'ValheimAutoModSync.Apply.cs' $item[1]
}
Write-Host '  PASS'

Write-Host '[2/8] Single installer/updater carries transactional engine...'
Require $installer '--apply-pending' 'installer updater mode'
Require $installer 'AutoModSyncApplyEngine.Run(_amsRoot)' 'installer updater mode'
Require $engine 'internal static int Run(string requestedAmsRoot)' 'transaction engine'
Forbid $engine 'static int Main(' 'transaction engine'
Require $releaseBuild 'AutoModSync.ApplyEngine.cs' 'release build'
Require $devBuild 'AutoModSync.ApplyEngine.cs' 'development build'
Require $releaseBuild 'ValheimAutoModSyncInstaller.exe' 'release build'
Require $modsite 'ValheimAutoModSyncInstaller.exe' 'Nexus/CurseForge package'
Write-Host '  PASS'

Write-Host '[3/8] Runtime updater is visible, same-user, and never force-kills Valheim...'
$applyLaunch = Section $client 'private static void BeginApplyAndRestart()' '// Intent: Publishes the verified pending-file list' 'client apply launch'
Require $applyLaunch 'FindInstallerUpdater(amsRoot)' 'client apply launch'
Require $client 'ValheimAutoModSyncInstaller.exe' 'client updater lookup'
Require $applyLaunch '--apply-pending' 'client apply launch'
Require $applyLaunch 'CreateNoWindow = false' 'client apply launch'
Require $applyLaunch 'ProcessWindowStyle.Normal' 'client apply launch'
Forbid $applyLaunch 'ProcessWindowStyle.Hidden' 'client apply launch'
Require $installer 'Waiting for Valheim to close normally' 'visible updater'
Require $manifest 'requestedExecutionLevel level="asInvoker"' 'installer manifest'
Forbid $manifest 'requireAdministrator' 'installer manifest'
Require $installer 'psi.Verb = "runas";' 'interactive install elevation'
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'Source') -Filter *.cs -File) {
    $sourceText = [IO.File]::ReadAllText($file.FullName)
    Forbid $sourceText '.Kill(' ('process safety ' + $file.Name)
    Forbid $sourceText 'Process.Kill' ('process safety ' + $file.Name)
}
Write-Host '  PASS'

Write-Host '[4/8] 2.6.0 -> 2.6.1 migration provides updater before new client depends on it...'
Require $server 'releaseInstallerUpdater' 'server migration payload'
Require $server 'ValheimAutoModSyncInstaller.exe' 'server migration payload'
Require $server 'cr.RelativePath = "ValheimAutoModSync.Client.dll"' 'server release client payload'
Require $server 'ir.RelativePath = "ValheimAutoModSyncInstaller.exe"' 'server release updater payload'
Require $client 'there is deliberately no fallback to legacy ValheimAutoModSync.Apply.exe' 'client migration policy'
Require $client 'RetireLegacyApplyHelper();' 'legacy helper retirement'
Require $client 'File.Delete(path);' 'legacy helper retirement'
Require $client 'Ignoring migration-only server installer/updater payload' 'local updater authority'
Require $client 'Repair or reinstall AutoModSync before joining.' 'missing updater fail-closed'
Require $client 'rather than ever scheduling the running updater for stale deletion' 'local updater ownership boundary'
Write-Host '  PASS'

Write-Host '[5/8] Nexus/CurseForge package has one AMS executable updater and no helper...'
Require $modsite '[ValidateSet("Nexus","CurseForge")]' 'mod-site target policy'
Require $modsite 'Copy-Item $InstallerExe' 'mod-site updater package'
Forbid $modsite '$ApplyExe' 'mod-site package'
Forbid $modsite '$ApplyIco' 'mod-site package'
Write-Host '  PASS'

Write-Host '[6/8] Mod-site copy accurately discloses core networking/updater behavior...'
foreach ($needle in @(
    'Server-to-client file synchronization is the core purpose of the mod',
    'not a generic web downloader',
    'does not force-kill Valheim or another process',
    'visible updater mode',
    'contains no arbitrary third-party gameplay mods'
)) {
    Require $modsiteReadme $needle 'mod-site reviewer copy'
}
Forbid $modsiteReadme 'the apply helper' 'mod-site reviewer copy'
Write-Host '  PASS'

Write-Host '[7/8] Legacy install.bat is only a transparent launcher...'
Require $installBat 'ValheimAutoModSyncInstaller.exe' 'install.bat'
Forbid $installBat 'curl' 'install.bat'
Forbid $installBat 'Invoke-WebRequest' 'install.bat'
Forbid $installBat 'BepInExPack' 'install.bat'
Forbid $installBat 'ValheimAutoModSync.Client.dll' 'install.bat'
Write-Host '  PASS'

Write-Host '[8/8] Apply safety remains transactional rather than being weakened by consolidation...'
foreach ($needle in @(
    'TransactionDirectoryName = "apply-transaction"',
    'PreparedMarkerName = "prepared.ok"',
    'CommittedMarkerName = "committed.ok"',
    'RecoverInterruptedTransaction',
    'PrepareTransaction',
    'ApplyPendingTransaction',
    'AutoModSyncPathSafety.EnsureNoReparsePoints'
)) {
    Require $engine $needle 'transaction engine'
}
Write-Host '  PASS'

Write-Host 'PASS: 2.6.1 uses one visible installer/updater executable, no standalone Apply.exe, no process-kill behavior, and preserves the transactional safety model.'
