param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('ArmProbe','InspectAdvertise')]
    [string]$Action,

    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx,

    [Parameter(Mandatory=$true)]
    [string]$ServerBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 server-browser presence development gate.
# Intent: arms a one-shot client-side structural probe and verifies that the dedicated server published the passive Steam rules used for future AMS badge discovery.
# Privacy: the probe logs only UI structure/type metadata and selected dedicated host/port already visible to the local player; it does not log the server signing fingerprint or player identifiers.

$clientRoot = [IO.Path]::GetFullPath($ClientBepInEx).TrimEnd('\','/')
$serverRoot = [IO.Path]::GetFullPath($ServerBepInEx).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $clientRoot -PathType Container)) { throw "Client BepInEx root not found: $clientRoot" }
if (-not (Test-Path -LiteralPath $serverRoot -PathType Container)) { throw "Server BepInEx root not found: $serverRoot" }

if ($Action -eq 'ArmProbe') {
    $amsRoot = Join-Path $clientRoot 'AutoModSync'
    if (-not (Test-Path -LiteralPath $amsRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $amsRoot -Force | Out-Null
    }

    $marker = Join-Path $amsRoot 'phase7-test-server-browser-probe.once'
    [IO.File]::WriteAllText($marker,'armed',[Text.UTF8Encoding]::new($false))

    Write-Host 'Phase 7 server-browser structural probe armed:'
    Write-Host ('  ' + $marker)
    Write-Host ''
    Write-Host 'Launch Valheim, open Join Game, select the known AMS favorite server, and leave the browser visible for a few seconds.'
    Write-Host 'The marker is consumed only after the live JoinPanel/ServerListGui exists.'
    return
}

$log = Join-Path $serverRoot 'LogOutput.log'
if (-not (Test-Path -LiteralPath $log -PathType Leaf)) { throw "Server log not found: $log" }
$matches = @(Select-String -LiteralPath $log -Pattern 'AutoModSync published Steam server-browser presence marker: version=([^,]+), protocol=([0-9]+)\.')
if ($matches.Count -eq 0) {
    throw 'No AutoModSync Steam server-browser presence publication was found. Restart the dedicated server with the current dev build, wait several seconds after Steam login, then rerun InspectAdvertise.'
}

$last = $matches[$matches.Count - 1]
Write-Host ('PASS server published passive AMS browser presence: ' + $last.Matches[0].Groups[1].Value + ' / protocol ' + $last.Matches[0].Groups[2].Value + '.')
Write-Host 'No signing fingerprint is part of the advertised rule marker.'
