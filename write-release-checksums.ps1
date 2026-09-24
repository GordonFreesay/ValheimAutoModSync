param(
    [string]$Root = $PSScriptRoot,
    [string]$OutputPath = '',
    [string[]]$Files = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Intent: Writes a stable SHA-256 manifest for official AutoModSync release packages using the shasum-compatible format accepted by GitHub artifact attestations.
# Scope: the manifest covers package bytes only; Authenticode status is separate and is never implied by this checksum file.

$rootPath = [IO.Path]::GetFullPath($Root)
$versionPath = Join-Path $rootPath 'VERSION'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
    throw "VERSION was not found under $rootPath"
}

$version = ([IO.File]::ReadAllText($versionPath)).Trim()
if ([String]::IsNullOrWhiteSpace($version)) { throw 'VERSION is empty.' }

$dist = Join-Path $rootPath 'Dist'
if ([String]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $dist 'SHA256SUMS.txt'
}
elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $rootPath $OutputPath
}

if ($Files.Count -eq 0) {
    $Files = @(
        (Join-Path $dist ("ValheimAutoModSync-$version.zip")),
        (Join-Path $dist ("ValheimAutoModSync-$version-Nexus.zip")),
        (Join-Path $dist ("ValheimAutoModSync-$version-CurseForge.zip")),
        (Join-Path $dist ("GordonFreesay-ValheimAutoModSync-$version.zip"))
    )
}

$resolved = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
foreach ($file in $Files) {
    $candidate = $file
    if (-not [IO.Path]::IsPathRooted($candidate)) { $candidate = Join-Path $rootPath $candidate }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Release artifact was not found: $candidate"
    }
    [void]$resolved.Add((Get-Item -LiteralPath $candidate))
}

$lines = New-Object 'System.Collections.Generic.List[string]'
foreach ($file in @($resolved | Sort-Object Name)) {
    $sha = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [void]$lines.Add(($sha + ' *' + $file.Name))
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [String]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
[IO.File]::WriteAllLines($OutputPath,$lines,[Text.UTF8Encoding]::new($false))

Write-Host ('Wrote SHA-256 release manifest: ' + $OutputPath)
$lines | ForEach-Object { Write-Host ('  ' + $_) }
