param(
    [Parameter(Mandatory = $true)]
    [string[]] $Files
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-SignTool {
    if ($env:AMS_SIGNTOOL) {
        if (-not (Test-Path -LiteralPath $env:AMS_SIGNTOOL -PathType Leaf)) {
            throw "AMS_SIGNTOOL does not exist: $env:AMS_SIGNTOOL"
        }
        return (Resolve-Path -LiteralPath $env:AMS_SIGNTOOL).Path
    }

    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }

    throw "signtool.exe was not found. Install the Windows SDK / signing client tools or set AMS_SIGNTOOL."
}

function Invoke-SignTool {
    param(
        [string] $Exe,
        [string[]] $Arguments
    )

    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed with exit code $LASTEXITCODE."
    }
}

foreach ($file in $Files) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Signing input does not exist: $file"
    }
}

$artifactDlib = $env:AMS_ARTIFACT_SIGNING_DLIB
$artifactMetadata = $env:AMS_ARTIFACT_SIGNING_METADATA
$pfx = $env:AMS_SIGN_PFX
$thumbprint = $env:AMS_SIGN_THUMBPRINT

$artifactMode = -not [string]::IsNullOrWhiteSpace($artifactDlib) -or -not [string]::IsNullOrWhiteSpace($artifactMetadata)
$pfxMode = -not [string]::IsNullOrWhiteSpace($pfx)
$storeMode = -not [string]::IsNullOrWhiteSpace($thumbprint)

$modeCount = 0
if ($artifactMode) { $modeCount++ }
if ($pfxMode) { $modeCount++ }
if ($storeMode) { $modeCount++ }
if ($modeCount -eq 0) { throw "No signing identity is configured. See SIGNING.md." }
if ($modeCount -gt 1) { throw "Configure exactly one signing mode: Artifact Signing, PFX, or certificate thumbprint." }

$signTool = Find-SignTool
$timestamp = if ($env:AMS_TIMESTAMP_URL) { $env:AMS_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }

foreach ($file in $Files) {
    Write-Host ""
    Write-Host "Signing $file" -ForegroundColor Cyan

    if ($artifactMode) {
        if (-not $artifactDlib -or -not $artifactMetadata) {
            throw "Artifact Signing requires both AMS_ARTIFACT_SIGNING_DLIB and AMS_ARTIFACT_SIGNING_METADATA."
        }
        if (-not (Test-Path -LiteralPath $artifactDlib -PathType Leaf)) {
            throw "Artifact Signing dlib not found: $artifactDlib"
        }
        if (-not (Test-Path -LiteralPath $artifactMetadata -PathType Leaf)) {
            throw "Artifact Signing metadata not found: $artifactMetadata"
        }

        Invoke-SignTool $signTool @(
            "sign", "/v",
            "/fd", "SHA256",
            "/tr", $timestamp,
            "/td", "SHA256",
            "/d", "Valheim AutoModSync",
            "/du", "https://github.com/GordonFreesay/ValheimAutoModSync",
            "/dlib", $artifactDlib,
            "/dmdf", $artifactMetadata,
            $file
        )
    }
    elseif ($pfxMode) {
        if (-not (Test-Path -LiteralPath $pfx -PathType Leaf)) {
            throw "PFX not found: $pfx"
        }

        $args = @(
            "sign", "/v",
            "/fd", "SHA256",
            "/tr", $timestamp,
            "/td", "SHA256",
            "/d", "Valheim AutoModSync",
            "/du", "https://github.com/GordonFreesay/ValheimAutoModSync",
            "/f", $pfx
        )
        if ($env:AMS_SIGN_PFX_PASSWORD) {
            $args += @("/p", $env:AMS_SIGN_PFX_PASSWORD)
        }
        $args += $file
        Invoke-SignTool $signTool $args
    }
    else {
        $args = @(
            "sign", "/v",
            "/fd", "SHA256",
            "/tr", $timestamp,
            "/td", "SHA256",
            "/d", "Valheim AutoModSync",
            "/du", "https://github.com/GordonFreesay/ValheimAutoModSync",
            "/sha1", ($thumbprint -replace "\s", ""),
            "/s", "My"
        )
        if ($env:AMS_SIGN_MACHINE_STORE -eq "1") {
            $args += "/sm"
        }
        $args += $file
        Invoke-SignTool $signTool $args
    }

    Write-Host "Verifying Authenticode signature..." -ForegroundColor DarkCyan
    Invoke-SignTool $signTool @("verify", "/pa", "/all", "/v", $file)
}

Write-Host ""
Write-Host "All AutoModSync-authored binaries were signed and verified." -ForegroundColor Green
