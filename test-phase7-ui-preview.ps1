param(
    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($ClientBepInEx).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Client BepInEx root not found: $root"
}

$amsRoot = Join-Path $root 'AutoModSync'
if (-not (Test-Path -LiteralPath $amsRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $amsRoot -Force | Out-Null
}

$marker = Join-Path $amsRoot 'phase7-test-ui-preview.once'
[System.IO.File]::WriteAllText($marker, '', (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host 'Phase 7 branded UI preview armed:' -ForegroundColor Yellow
Write-Host ('  ' + $marker)
Write-Host ''
Write-Host 'Start Valheim with the current AMS_DEV_TESTS client. The preview will cycle through 10 UI states at the main menu, then clear itself.'
Write-Host 'The marker is one-shot and is not compiled into release binaries.'
