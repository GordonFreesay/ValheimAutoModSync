$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-AutoModSyncVersion {
    param([string]$Root = $PSScriptRoot)

    $path = Join-Path $Root "VERSION"
    if (-not (Test-Path -LiteralPath $path)) { throw "VERSION file is missing: $path" }
    $version = ([System.IO.File]::ReadAllText($path)).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Invalid semantic version in VERSION: $version"
    }
    return $version
}

function Invoke-AutoModSyncStandaloneBuild {
    param([string]$Root = $PSScriptRoot)

    $oldNoPause = $env:AMS_NO_PAUSE
    try {
        $env:AMS_NO_PAUSE = "1"
        & (Join-Path $Root "build-release.bat")
        if ($LASTEXITCODE -ne 0) { throw "build-release.bat failed with exit code $LASTEXITCODE" }
    }
    finally {
        $env:AMS_NO_PAUSE = $oldNoPause
    }
}

function New-AutoModSyncZip {
    param(
        [Parameter(Mandatory=$true)][string]$SourceDirectory,
        [Parameter(Mandatory=$true)][string]$DestinationZip
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\','/')
    if (Test-Path -LiteralPath $DestinationZip) { Remove-Item -LiteralPath $DestinationZip -Force }

    $stream = [System.IO.File]::Open($DestinationZip, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        foreach ($item in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
            $relative = $item.FullName.Substring($source.Length).TrimStart('\','/').Replace('\','/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $fileStream = [System.IO.File]::OpenRead($item.FullName)
            try { $fileStream.CopyTo($entryStream) }
            finally {
                $fileStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        if ($archive -ne $null) { $archive.Dispose() }
        $stream.Dispose()
    }
}

function Assert-NoNestedArchives {
    param([Parameter(Mandatory=$true)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::OpenRead($ZipPath)
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        $nested = @($archive.Entries | Where-Object { $_.FullName -match '\.(zip|7z|rar|tar|gz|bz2|xz)$' })
        if ($nested.Count -gt 0) {
            throw "Nested archives are not allowed in this package: $($nested.FullName -join ', ')"
        }
    }
    finally {
        if ($archive -ne $null) { $archive.Dispose() }
        $stream.Dispose()
    }
}
