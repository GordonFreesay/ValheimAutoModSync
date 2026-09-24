param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare','Inspect','Cleanup')]
    [string]$Action,

    [Parameter(Mandatory=$true)]
    [string]$ServerBepInEx,

    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$statePath = Join-Path $env:TEMP 'AMS26-Phase1-Legacy25-State.json'
$serverAms = Join-Path $ServerBepInEx 'AutoModSync'
$clientAms = Join-Path $ClientBepInEx 'AutoModSync'
$legacyMarker = Join-Path $serverAms 'resume-test-emulate-legacy-server.once'
$applyFailureMarker = Join-Path $clientAms 'phase1-test-fail-apply-prep.once'
$serverFixtureDir = Join-Path (Join-Path $ServerBepInEx 'plugins') '__AMS_PHASE1_LEGACY25__'
$serverFixture = Join-Path $serverFixtureDir 'payload.txt'
$clientFixtureDir = Join-Path (Join-Path $ClientBepInEx 'plugins') '__AMS_PHASE1_LEGACY25__'
$clientFixture = Join-Path $clientFixtureDir 'payload.txt'
$clientStagedDir = Join-Path (Join-Path (Join-Path $clientAms 'staging') 'plugins') '__AMS_PHASE1_LEGACY25__'
$clientStagedFixture = Join-Path $clientStagedDir 'payload.txt.amsnew'
$clientPending = Join-Path $clientAms 'pending.txt'
$clientLog = Join-Path $ClientBepInEx 'LogOutput.log'
$serverLog = Join-Path $ServerBepInEx 'LogOutput.log'

function Ensure-Roots {
    if (-not (Test-Path -LiteralPath $ServerBepInEx -PathType Container)) { throw "Server BepInEx root not found: $ServerBepInEx" }
    if (-not (Test-Path -LiteralPath $ClientBepInEx -PathType Container)) { throw "Client BepInEx root not found: $ClientBepInEx" }
    New-Item -ItemType Directory -Path $serverAms,$clientAms -Force | Out-Null
}

function Read-SharedLogLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }

    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        )
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $false)
        $lines = New-Object System.Collections.Generic.List[string]
        while (-not $reader.EndOfStream) { [void]$lines.Add($reader.ReadLine()) }
        return @($lines.ToArray())
    }
    finally {
        if ($reader -ne $null) { $reader.Dispose() }
        elseif ($stream -ne $null) { $stream.Dispose() }
    }
}

function Get-LineCount([string]$Path) {
    return @(Read-SharedLogLines $Path).Count
}

function Get-NewLines([string]$Path, [int]$StartCount) {
    $all = @(Read-SharedLogLines $Path)
    if ($StartCount -lt 0) { $StartCount = 0 }
    if ($all.Length -lt $StartCount) { return $all }
    if ($StartCount -ge $all.Length) { return @() }
    return @($all[$StartCount..($all.Length - 1)])
}

function Get-Sha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Prepare-Test {
    Ensure-Roots

    if (Test-Path -LiteralPath $clientPending -PathType Leaf) {
        throw "Client has an existing pending apply: $clientPending . Resolve that recovery state before the 2.5 compatibility test."
    }
    if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
        throw "Reserved client live fixture already exists: $clientFixture . Run Cleanup and investigate before testing."
    }

    foreach ($p in @($legacyMarker,$applyFailureMarker)) {
        try { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } } catch { }
    }
    try { if (Test-Path -LiteralPath $clientStagedDir) { Remove-Item -LiteralPath $clientStagedDir -Recurse -Force } } catch { }

    New-Item -ItemType Directory -Path $serverFixtureDir -Force | Out-Null
    [IO.File]::WriteAllText($serverFixture, "AutoModSync 2.5 fallback live fixture" + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    New-Item -ItemType File -Path $legacyMarker -Force | Out-Null
    New-Item -ItemType File -Path $applyFailureMarker -Force | Out-Null

    $state = [ordered]@{
        clientLogLines = (Get-LineCount $clientLog)
        serverLogLines = (Get-LineCount $serverLog)
        serverFixtureSha256 = (Get-Sha256 $serverFixture)
        preparedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host ''
    Write-Host 'ARMED: 2.6 client -> emulated exact 2.5.0 AMS4 wire compatibility test.'
    Write-Host 'The server fixture is missing from the client, so this join must perform a real legacy-shaped bundle transfer.'
    Write-Host 'A client dev hook will stop after verified extraction but before durable apply state, so no live synchronized file should change.'
    Write-Host ''
    Write-Host 'Start the dedicated server and Valheim, join once, wait for AutoModSync to disconnect the protected join, then run -Action Inspect.'
}

function Inspect-Test {
    Ensure-Roots
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'No prepared legacy-2.5 test state exists. Run -Action Prepare first.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json

    $newClient = @(Get-NewLines $clientLog ([int]$state.clientLogLines))
    $newServer = @(Get-NewLines $serverLog ([int]$state.serverLogLines))
    $clientText = $newClient -join [Environment]::NewLine
    $serverText = $newServer -join [Environment]::NewLine
    $ok = $true

    Write-Host ''
    Write-Host 'Inspecting 2.6 client -> 2.5 AMS4 fallback compatibility...'

    $checks = @(
        @('server', "AutoModSync DEV TEST emulating a pre-resume AMS4 server for this peer.", 'server selected the 2.5 wire-shape emulator'),
        @('server', "AutoModSync DEV TEST legacy-server compatibility confirmed: original AMS4 bundle request/header shape active.", 'server observed the original 2.5 bundle request/header shape'),
        @('client', "AutoModSync DEV TEST legacy-server compatibility confirmed: 2.5 AMS4 capability set accepted; resume/scheduler extensions remain disabled.", '2.6 client accepted the 2.5 capability set'),
        @('client', "AutoModSync using pipelined binary bundle transfer", '2.6 client selected the existing 2.5 pipelined transfer fallback'),
        @('client', "AutoModSync compressed package verified and unpacked:", 'legacy-shaped bundle completed full verification/extraction'),
        @('client', "Mods downloaded but automatic apply/restart failed: DEV TEST forced apply/restart preparation failure", 'test stopped before durable/live apply')
    )

    foreach ($check in $checks) {
        $scope = $check[0]
        $needle = $check[1]
        $description = $check[2]
        $haystack = if ($scope -eq 'server') { $serverText } else { $clientText }
        if ($haystack.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Write-Host ("  PASS {0}." -f $description)
        }
        else {
            Write-Host ("  FAIL missing evidence: {0}" -f $description)
            $ok = $false
        }
    }

    if ($clientText.IndexOf('AutoModSync released the original Valheim ServerHandshake', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Write-Host '  FAIL vanilla ServerHandshake was replayed during the deliberately aborted protected sync.'
        $ok = $false
    }
    else {
        Write-Host '  PASS protected sync failure did not replay vanilla ServerHandshake.'
    }

    if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
        Write-Host ("  FAIL test payload reached the live client plugins tree: {0}" -f $clientFixture)
        $ok = $false
    }
    else {
        Write-Host '  PASS no test payload reached the live client plugins tree.'
    }

    if (Test-Path -LiteralPath $clientPending -PathType Leaf) {
        Write-Host ("  FAIL durable pending apply exists: {0}" -f $clientPending)
        $ok = $false
    }
    else {
        Write-Host '  PASS no durable pending apply was accepted.'
    }

    if (-not (Test-Path -LiteralPath $clientStagedFixture -PathType Leaf)) {
        Write-Host ("  FAIL verified staged transfer fixture is missing: {0}" -f $clientStagedFixture)
        $ok = $false
    }
    else {
        $stageHash = Get-Sha256 $clientStagedFixture
        if ($stageHash -ne [string]$state.serverFixtureSha256) {
            Write-Host '  FAIL staged fixture SHA-256 does not match the server source.'
            $ok = $false
        }
        else {
            Write-Host '  PASS staged fixture SHA-256 exactly matches the server source.'
        }
    }

    if (Test-Path -LiteralPath $legacyMarker) {
        Write-Host '  FAIL server legacy marker was not consumed.'
        $ok = $false
    }
    else {
        Write-Host '  PASS server legacy marker was consumed.'
    }

    if (Test-Path -LiteralPath $applyFailureMarker) {
        Write-Host '  FAIL client apply-preparation marker was not consumed.'
        $ok = $false
    }
    else {
        Write-Host '  PASS client apply-preparation marker was consumed.'
    }

    if ($ok) {
        Write-Host ''
        Write-Host 'PASS: 2.6 client completed the 2.5 AMS4 transfer fallback and remained safe before live apply.'
        Write-Host 'Stop Valheim and the dedicated server, then run -Action Cleanup.'
        return
    }

    Write-Host ''
    Write-Host 'Relevant new client AutoModSync lines:'
    $newClient | Where-Object { $_ -match 'AutoModSync' } | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
    Write-Host ''
    Write-Host 'Relevant new server AutoModSync lines:'
    $newServer | Where-Object { $_ -match 'AutoModSync' } | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
    exit 1
}

function Cleanup-Test {
    Ensure-Roots
    foreach ($p in @($legacyMarker,$applyFailureMarker)) {
        try { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } } catch { }
    }
    try { if (Test-Path -LiteralPath $serverFixtureDir) { Remove-Item -LiteralPath $serverFixtureDir -Recurse -Force } } catch { }
    try { if (Test-Path -LiteralPath $clientStagedDir) { Remove-Item -LiteralPath $clientStagedDir -Recurse -Force } } catch { }
    try { if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force } } catch { }

    if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
        Write-Warning "Reserved LIVE client fixture exists and was not deleted automatically: $clientFixture"
    }
    if (Test-Path -LiteralPath $clientPending -PathType Leaf) {
        Write-Warning "pending.txt exists and was not deleted automatically: $clientPending"
    }

    Write-Host 'Phase 1 legacy-2.5 compatibility markers/server fixture/staged fixture cleaned.'
}

switch ($Action) {
    'Prepare' { Prepare-Test }
    'Inspect' { Inspect-Test }
    'Cleanup' { Cleanup-Test }
}
