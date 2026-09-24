param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('StartSuite','Next','Prepare','Inspect','Cleanup')]
    [string]$Action,

    [ValidateSet('bad-ack','ack-no-manifest','bad-manifest-header','missing-manifest-part','bad-signature','trust-decline','server-error','bad-bundle-header','bad-legacy-chunk','bad-binary-batch','bad-bundle-end','apply-prep-failure')]
    [string]$Mode,

    [Parameter(Mandatory=$true)]
    [string]$ServerBepInEx,

    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$modes = @(
    'bad-ack',
    'ack-no-manifest',
    'bad-manifest-header',
    'missing-manifest-part',
    'bad-signature',
    'trust-decline',
    'server-error',
    'bad-bundle-header',
    'bad-legacy-chunk',
    'bad-binary-batch',
    'bad-bundle-end',
    'apply-prep-failure'
)

$clientExpected = @{
    'bad-ack'               = 'Invalid AutoModSync preflight acknowledgement'
    'ack-no-manifest'       = 'acknowledged preflight but did not begin a manifest'
    'bad-manifest-header'   = 'Bad AutoModSync manifest header'
    'missing-manifest-part' = 'AutoModSync manifest verification failed: Manifest was incomplete'
    'bad-signature'         = 'AutoModSync manifest verification failed'
    'trust-decline'         = 'AutoModSync server identity was not trusted by the user'
    'server-error'          = 'DEV TEST server-reported AMS error after recognition'
    'bad-bundle-header'     = 'Could not prepare compressed mod download'
    'bad-legacy-chunk'      = 'Compressed mod package download failed: Out-of-order compressed package chunk'
    'bad-binary-batch'      = 'Compressed mod package batch download failed: Invalid compressed package batch'
    'bad-bundle-end'        = 'Compressed mod package verification failed: Compressed package completion did not match its header'
    'apply-prep-failure'    = 'Mods downloaded but automatic apply/restart failed: DEV TEST forced apply/restart preparation failure'
}

$statePath = Join-Path $env:TEMP 'AMS26-Phase1-FailClosed-State.json'
$serverAms = Join-Path $ServerBepInEx 'AutoModSync'
$clientAms = Join-Path $ClientBepInEx 'AutoModSync'
$serverMarker = Join-Path $serverAms 'phase1-failclosed-mode.once'
$clientTrustMarker = Join-Path $clientAms 'phase1-test-force-trust-prompt.once'
$clientApplyMarker = Join-Path $clientAms 'phase1-test-fail-apply-prep.once'
$serverFixtureDir = Join-Path (Join-Path $ServerBepInEx 'plugins') '__AMS_PHASE1_FAILCLOSED__'
$serverFixture = Join-Path $serverFixtureDir 'payload.txt'
$clientFixtureDir = Join-Path (Join-Path $ClientBepInEx 'plugins') '__AMS_PHASE1_FAILCLOSED__'
$clientFixture = Join-Path $clientFixtureDir 'payload.txt'
$clientStagedFixture = Join-Path (Join-Path (Join-Path $clientAms 'staging') 'plugins') '__AMS_PHASE1_FAILCLOSED__\payload.txt.amsnew'
$clientPending = Join-Path $clientAms 'pending.txt'
$clientLog = Join-Path $ClientBepInEx 'LogOutput.log'
$serverLog = Join-Path $ServerBepInEx 'LogOutput.log'

function Ensure-Roots {
    if (-not (Test-Path -LiteralPath $ServerBepInEx -PathType Container)) { throw "Server BepInEx root not found: $ServerBepInEx" }
    if (-not (Test-Path -LiteralPath $ClientBepInEx -PathType Container)) { throw "Client BepInEx root not found: $ClientBepInEx" }
    New-Item -ItemType Directory -Path $serverAms,$clientAms -Force | Out-Null
}

function Get-LineCount([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 0 }
    return @([IO.File]::ReadAllLines($Path)).Count
}

function Get-NewLines([string]$Path, [int]$StartCount) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $all = [IO.File]::ReadAllLines($Path)
    if ($StartCount -lt 0) { $StartCount = 0 }
    if ($StartCount -ge $all.Length) { return @() }
    return @($all[$StartCount..($all.Length - 1)])
}

function Remove-TestMarkers {
    foreach ($p in @($serverMarker,$clientTrustMarker,$clientApplyMarker)) {
        try { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } } catch { }
    }
}

function Write-State([string]$CurrentMode, [int]$Index) {
    $state = [ordered]@{
        mode = $CurrentMode
        index = $Index
        clientLogLines = (Get-LineCount $clientLog)
        serverLogLines = (Get-LineCount $serverLog)
        serverBepInEx = $ServerBepInEx
        clientBepInEx = $ClientBepInEx
        preparedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Read-State {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "No active Phase 1 fail-closed suite state exists. Run -Action StartSuite first." }
    return (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json)
}

function Ensure-Fixture {
    if (Test-Path -LiteralPath $clientPending -PathType Leaf) {
        throw "Client has an existing pending apply: $clientPending . Resolve that recovery state before fail-closed testing."
    }
    if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
        throw "The reserved client live fixture already exists: $clientFixture . Run -Action Cleanup first and verify why it existed."
    }

    New-Item -ItemType Directory -Path $serverFixtureDir -Force | Out-Null
    [IO.File]::WriteAllText($serverFixture, "AutoModSync Phase 1 fail-closed fixture" + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

    try { if (Test-Path -LiteralPath $clientStagedFixture) { Remove-Item -LiteralPath $clientStagedFixture -Force } } catch { }
}

function Prepare-Mode([string]$CurrentMode, [int]$Index) {
    Ensure-Roots
    Remove-TestMarkers

    if (-not ($modes -contains $CurrentMode)) { throw "Unknown fail-closed mode: $CurrentMode" }

    [IO.File]::WriteAllText($serverMarker, $CurrentMode + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

    if ($CurrentMode -eq 'trust-decline') {
        New-Item -ItemType File -Path $clientTrustMarker -Force | Out-Null
    }
    if ($CurrentMode -eq 'apply-prep-failure') {
        New-Item -ItemType File -Path $clientApplyMarker -Force | Out-Null
    }

    Write-State $CurrentMode $Index

    Write-Host ''
    Write-Host ("ARMED [{0}/{1}]: {2}" -f ($Index + 1), $modes.Count, $CurrentMode)
    if ($CurrentMode -eq 'ack-no-manifest') {
        Write-Host 'Join once and wait at least 16 seconds; this case intentionally waits for the recognized-server manifest timeout.'
    }
    elseif ($CurrentMode -eq 'trust-decline') {
        Write-Host 'Join once. When the native AutoModSync trust dialog opens, click No.'
    }
    else {
        Write-Host 'Join once and wait for AutoModSync to block/disconnect the join.'
    }
    Write-Host 'Then run the same command with -Action Next. Next will inspect this run and arm the following case only if it passed.'
}

function Inspect-Current([object]$State) {
    $current = [string]$State.mode
    $newClient = Get-NewLines $clientLog ([int]$State.clientLogLines)
    $newServer = Get-NewLines $serverLog ([int]$State.serverLogLines)
    $clientText = ($newClient -join [Environment]::NewLine)
    $serverText = ($newServer -join [Environment]::NewLine)
    $expected = [string]$clientExpected[$current]

    $ok = $true
    Write-Host ''
    Write-Host ("Inspecting Phase 1 fail-closed mode: {0}" -f $current)

    if ($clientText.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Write-Host ("  PASS client rejected at expected boundary: {0}" -f $expected)
    }
    else {
        Write-Host ("  FAIL expected client evidence not found: {0}" -f $expected)
        $ok = $false
    }

    $armed = "AutoModSync DEV FAIL-CLOSED armed mode '$current' for this peer."
    if ($serverText.IndexOf($armed, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Write-Host '  PASS server consumed the requested development fault mode.'
    }
    else {
        Write-Host '  FAIL server did not log consumption of the requested fault mode.'
        $ok = $false
    }

    if ($clientText.IndexOf('AutoModSync released the original Valheim ServerHandshake', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Write-Host '  FAIL vanilla ServerHandshake was replayed during a recognized fail-closed case.'
        $ok = $false
    }
    else {
        Write-Host '  PASS no vanilla ServerHandshake replay was logged for this faulted session.'
    }

    if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
        Write-Host ("  FAIL reserved live fixture exists: {0}" -f $clientFixture)
        $ok = $false
    }
    else {
        Write-Host '  PASS no test payload reached the live client plugins tree.'
    }

    if (Test-Path -LiteralPath $clientPending -PathType Leaf) {
        Write-Host ("  FAIL pending apply state exists after fail-closed test: {0}" -f $clientPending)
        $ok = $false
    }
    else {
        Write-Host '  PASS no durable pending apply was accepted.'
    }

    if ($ok) {
        Write-Host ("PASS: {0} remained fail-closed." -f $current)
        return $true
    }

    Write-Host ''
    Write-Host 'Relevant new client AutoModSync lines:'
    $newClient | Where-Object { $_ -match 'AutoModSync' } | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
    Write-Host ''
    Write-Host 'Relevant new server AutoModSync lines:'
    $newServer | Where-Object { $_ -match 'AutoModSync' } | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
    return $false
}

function Cleanup-Suite {
    Ensure-Roots
    Remove-TestMarkers

    try { if (Test-Path -LiteralPath $serverFixtureDir) { Remove-Item -LiteralPath $serverFixtureDir -Recurse -Force } } catch { }
    try { if (Test-Path -LiteralPath (Split-Path -Parent $clientStagedFixture)) { Remove-Item -LiteralPath (Split-Path -Parent $clientStagedFixture) -Recurse -Force } } catch { }

    if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
        Write-Warning "The reserved LIVE client fixture exists. This means a fail-closed test unexpectedly applied content. It was NOT deleted automatically: $clientFixture"
    }
    if (Test-Path -LiteralPath $clientPending -PathType Leaf) {
        Write-Warning "pending.txt exists and was NOT deleted automatically: $clientPending"
    }

    try { if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force } } catch { }
    Write-Host 'Phase 1 fail-closed test markers/server fixture/staged fixture cleaned.'
}

Ensure-Roots

switch ($Action) {
    'StartSuite' {
        Remove-TestMarkers
        if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force }
        Ensure-Fixture
        Prepare-Mode $modes[0] 0
        Write-Host ''
        Write-Host 'IMPORTANT: StartSuite should be run while Valheim and the dedicated server are stopped. Start both now, then perform the join described above.'
    }

    'Prepare' {
        if ([String]::IsNullOrEmpty($Mode)) { throw '-Mode is required with -Action Prepare.' }
        Ensure-Fixture
        $idx = [Array]::IndexOf($modes, $Mode)
        Prepare-Mode $Mode $idx
    }

    'Inspect' {
        $state = Read-State
        if (-not (Inspect-Current $state)) { exit 1 }
    }

    'Next' {
        $state = Read-State
        if (-not (Inspect-Current $state)) {
            Write-Host ''
            Write-Host 'Suite did not advance. Fix/repeat this mode, then run -Action Next again.'
            exit 1
        }

        $next = [int]$state.index + 1
        if ($next -ge $modes.Count) {
            Write-Host ''
            Write-Host 'PASS: all 12 recognized-AMS fail-closed live cases passed.'
            Write-Host 'Stop Valheim and the dedicated server, then run -Action Cleanup.'
            exit 0
        }

        Prepare-Mode $modes[$next] $next
    }

    'Cleanup' {
        Cleanup-Suite
    }
}
