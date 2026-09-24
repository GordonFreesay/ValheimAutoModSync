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
$srcApply = Join-Path $devRoot 'ValheimAutoModSync.Apply.exe'

foreach ($path in @($srcClient,$srcServer,$srcApply)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ("Development runtime is missing: " + $path + [Environment]::NewLine + "Run .\\build-dev.bat first.")
    }
}

function Normalize-Root([string]$Path,[string]$Label) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\\','/')
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

# Package-manager installs keep Apply.exe beside the client DLL. Standalone installs keep it in BepInEx\AutoModSync.
$clientPluginDir = Split-Path -Parent $clientTarget
$packagedApply = Join-Path $clientPluginDir 'ValheimAutoModSync.Apply.exe'
if (Test-Path -LiteralPath $packagedApply -PathType Leaf) {
    $applyTarget = $packagedApply
} else {
    $applyTarget = Join-Path (Join-Path $clientRoot 'AutoModSync') 'ValheimAutoModSync.Apply.exe'
    $applyParent = Split-Path -Parent $applyTarget
    if (-not (Test-Path -LiteralPath $applyParent -PathType Container)) {
        New-Item -ItemType Directory -Path $applyParent -Force | Out-Null
    }
}

$rows = @()
$rows += Copy-Verified $srcClient $clientTarget 'Client'
$rows += Copy-Verified $srcApply $applyTarget 'Apply helper'

if (-not [String]::IsNullOrWhiteSpace($ServerBepInEx)) {
    $serverRoot = Normalize-Root $ServerBepInEx 'Server BepInEx root'
    $serverPlugins = Join-Path $serverRoot 'plugins'
    $serverTarget = Resolve-SingleInstalledFile $serverPlugins 'ValheimAutoModSync.Server.dll' (Join-Path $serverPlugins 'ValheimAutoModSync.Server.dll') 'Server plugin'
    $rows += Copy-Verified $srcServer $serverTarget 'Server'
}

Write-Host ''
Write-Host 'Deployed AutoModSync 2.6 development runtime:'
$rows | Format-Table -AutoSize
Write-Host ''
Write-Host 'Hashes match DevBuild. Restart the dedicated server and Valheim before the next live test.'
