param(
    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx,

    [Parameter(Mandatory=$false)]
    [string]$ServerBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 development runtime deployer.
# Intent: copy the binaries produced by build-dev.bat into the exact currently-installed AMS locations used for live validation.
# Safety: refuses ambiguous duplicate client/server plugin installations instead of guessing which copy BepInEx will load.

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$devRoot = Join-Path $repoRoot 'DevBuild'
$srcClient = Join-Path $devRoot 'ValheimAutoModSync.Client.dll'
$srcServer = Join-Path $devRoot 'ValheimAutoModSync.Server.dll'
$srcInstaller = Join-Path $devRoot 'ValheimAutoModSyncInstaller.exe'

foreach ($path in @($srcClient,$srcServer,$srcInstaller)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ("Development runtime is missing: " + $path + [Environment]::NewLine + "Run .\\build-dev.bat first.")
    }
}

function Normalize-Root([string]$Path,[string]$Label) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw ($Label + " not found: " + $full) }
    return $full
}

function Resolve-SingleInstalledFile([string]$SearchRoot,[string]$FileName,[string]$FallbackPath,[string]$Label) {
    $matches = @()
    if (Test-Path -LiteralPath $SearchRoot -PathType Container) {
        $matches = @(Get-ChildItem -LiteralPath $SearchRoot -Filter $FileName -File -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    }

    if ($matches.Count -gt 1) {
        throw ($Label + " is ambiguous; multiple installed copies were found:" + [Environment]::NewLine + "  " + ($matches -join ([Environment]::NewLine + "  ")))
    }
    if ($matches.Count -eq 1) { return $matches[0] }

    $parent = Split-Path -Parent $FallbackPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    return $FallbackPath
}

function Copy-Verified([string]$Source,[string]$Destination,[string]$Label) {
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Source).Hash.ToLowerInvariant()
    $destHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash.ToLowerInvariant()
    if ($sourceHash -ne $destHash) { throw ($Label + " hash verification failed after copy.") }

    [pscustomobject]@{
        Component = $Label
        Destination = $Destination
        Sha256 = $destHash
    }
}

$clientRoot = Normalize-Root $ClientBepInEx 'Client BepInEx root'
$clientPlugins = Join-Path $clientRoot 'plugins'
$clientTarget = Resolve-SingleInstalledFile $clientPlugins 'ValheimAutoModSync.Client.dll' (Join-Path $clientPlugins 'ValheimAutoModSync.Client.dll') 'Client plugin'

# The installer/updater is always deployed beside the active client DLL so package-managed and standalone layouts use one lookup rule.
$clientPluginDir = Split-Path -Parent $clientTarget
$installerTarget = Join-Path $clientPluginDir 'ValheimAutoModSyncInstaller.exe'

$rows = @()
$rows += Copy-Verified $srcClient $clientTarget 'Client'
$rows += Copy-Verified $srcInstaller $installerTarget 'Installer/updater'

if (-not [String]::IsNullOrWhiteSpace($ServerBepInEx)) {
    $serverRoot = Normalize-Root $ServerBepInEx 'Server BepInEx root'
    $serverPlugins = Join-Path $serverRoot 'plugins'
    $serverTarget = Resolve-SingleInstalledFile $serverPlugins 'ValheimAutoModSync.Server.dll' (Join-Path $serverPlugins 'ValheimAutoModSync.Server.dll') 'Server plugin'
    $rows += Copy-Verified $srcServer $serverTarget 'Server'

    # Keep the server-distributed client payload on the exact same dev build. Otherwise the first live join can
    # immediately "self-update" the freshly deployed client back to an older Client.dll from ClientPayload/release.
    $serverAms = Join-Path $serverRoot 'AutoModSync'
    $clientPayloadTarget = Join-Path (Join-Path $serverAms 'ClientPayload\plugins') 'ValheimAutoModSync.Client.dll'
    $clientPayloadParent = Split-Path -Parent $clientPayloadTarget
    if (-not (Test-Path -LiteralPath $clientPayloadParent -PathType Container)) {
        New-Item -ItemType Directory -Path $clientPayloadParent -Force | Out-Null
    }
    $rows += Copy-Verified $srcClient $clientPayloadTarget 'Server client payload'

    $releaseRoot = Join-Path $serverAms 'release'
    $releaseTarget = Join-Path $releaseRoot 'ValheimAutoModSync.Client.dll'
    $releaseInstaller = Join-Path $releaseRoot 'ValheimAutoModSyncInstaller.exe'
    if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    }
    $rows += Copy-Verified $srcClient $releaseTarget 'Server release client payload'
    $rows += Copy-Verified $srcInstaller $releaseInstaller 'Server release installer/updater payload'
}

Write-Host ''
Write-Host 'Deployed AutoModSync 2.6 development runtime:'
$rows | Format-Table -AutoSize
Write-Host ''
Write-Host 'Hashes match DevBuild. Restart the dedicated server and Valheim before the next live test.'
