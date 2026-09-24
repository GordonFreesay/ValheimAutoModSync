param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare','Inspect','PrepareCleanup','InspectCleanup')]
    [string]$Action,

    [Parameter(Mandatory=$true)]
    [string]$ServerBepInEx,

    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 7 real-event telemetry/UI harness.
# Intent: creates one bounded, inert client-only payload large enough to keep the real download UI visible,
# then verifies production transfer/apply/restart/reconnect evidence without touching real mod DLLs/config/trust.
# Safety: fixture paths are fixed beneath __AMS_PHASE7_UI__; cleanup is performed by AutoModSync ownership,
# not by directly deleting the synchronized client file.

$serverRoot = [IO.Path]::GetFullPath($ServerBepInEx).TrimEnd('\','/')
$clientRoot = [IO.Path]::GetFullPath($ClientBepInEx).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $serverRoot -PathType Container)) { throw "Server BepInEx root not found: $serverRoot" }
if (-not (Test-Path -LiteralPath $clientRoot -PathType Container)) { throw "Client BepInEx root not found: $clientRoot" }

$serverTestRoot = Join-Path $serverRoot 'AutoModSync\ClientPayload\plugins\__AMS_PHASE7_UI__'
$serverFixture = Join-Path $serverTestRoot 'phase7-telemetry.bin'
$clientFixture = Join-Path $clientRoot 'plugins\__AMS_PHASE7_UI__\phase7-telemetry.bin'
$clientAms = Join-Path $clientRoot 'AutoModSync'
$clientPending = Join-Path $clientAms 'pending.txt'
$clientTransaction = Join-Path $clientAms 'apply-transaction'
$clientLog = Join-Path $clientRoot 'LogOutput.log'
$serverLog = Join-Path $serverRoot 'LogOutput.log'
$applyLog = Join-Path $clientAms 'apply.log'
$statePath = Join-Path $env:TEMP 'AMS26-Phase7-Live-State.json'
$fixtureBytes = 128L * 1024L * 1024L

function Read-SharedLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader = [IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true,4096,$false)
        $list = New-Object 'System.Collections.Generic.List[string]'
        while (-not $reader.EndOfStream) { [void]$list.Add($reader.ReadLine()) }
        return @($list.ToArray())
    }
    finally {
        if ($reader -ne $null) { $reader.Dispose() }
        elseif ($stream -ne $null) { $stream.Dispose() }
    }
}

function Line-Count([string]$Path) { return @(Read-SharedLines $Path).Count }

function New-Lines([string]$Path,[int]$Start) {
    $all = @(Read-SharedLines $Path)
    if ($Start -lt 0) { $Start = 0 }
    if ($all.Length -lt $Start) { return $all }
    if ($Start -ge $all.Length) { return @() }
    return @($all[$Start..($all.Length-1)])
}

function Read-Phase7Ownership {
    $ownershipRoot = Join-Path $clientAms 'ownership'
    $rows = @()
    if (-not (Test-Path -LiteralPath $ownershipRoot -PathType Container)) { return $rows }

    Get-ChildItem -LiteralPath $ownershipRoot -Filter '*.txt' -File -ErrorAction SilentlyContinue | ForEach-Object {
        $lines = @(Get-Content -LiteralPath $_.FullName)
        if ($lines.Count -lt 2 -or $lines[0] -ne 'AMSOWN1') { return }
        $fingerprint = $lines[1].Trim()
        foreach ($line in $lines | Select-Object -Skip 2) {
            if ([String]::IsNullOrWhiteSpace($line)) { continue }
            $parts = $line -split '\|',4
            if ($parts.Count -ne 4) { continue }
            try { $rel = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[3])) } catch { continue }
            if ($rel -ieq '__AMS_PHASE7_UI__\phase7-telemetry.bin' -or $rel -ieq '__AMS_PHASE7_UI__/phase7-telemetry.bin') {
                $rows += [pscustomobject]@{
                    Fingerprint = $fingerprint
                    Kind = $parts[0]
                    Size = [Int64]$parts[1]
                    Sha256 = $parts[2].ToLowerInvariant()
                    Path = $rel
                }
            }
        }
    }
    return $rows
}

function Write-Fixture {
    if (-not (Test-Path -LiteralPath $serverTestRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $serverTestRoot -Force | Out-Null
    }

    $buffer = New-Object byte[] (1024 * 1024)
    $random = [Random]::new(2607007)
    $random.NextBytes($buffer)

    $stream = [IO.File]::Open($serverFixture,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        for ($i = 0; $i -lt 128; $i++) {
            $stream.Write($buffer,0,$buffer.Length)
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Require-Clean-Apply-State {
    if (Test-Path -LiteralPath $clientPending -PathType Leaf) { throw "Existing AutoModSync pending apply must be resolved first: $clientPending" }
    if (Test-Path -LiteralPath $clientTransaction) { throw "Existing AutoModSync apply transaction must be resolved first: $clientTransaction" }
}

function Require-Matching-Client-Payloads {
    $clientPlugins = Join-Path $clientRoot 'plugins'
    $installed = @(Get-ChildItem -LiteralPath $clientPlugins -Filter 'ValheimAutoModSync.Client.dll' -File -Recurse -ErrorAction SilentlyContinue)
    if ($installed.Count -ne 1) {
        throw ("Expected exactly one installed ValheimAutoModSync.Client.dll under " + $clientPlugins + "; found " + $installed.Count + ".")
    }

    $installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installed[0].FullName).Hash.ToLowerInvariant()
    $serverAms = Join-Path $serverRoot 'AutoModSync'
    $payloads = @(
        (Join-Path $serverAms 'ClientPayload\plugins\ValheimAutoModSync.Client.dll'),
        (Join-Path $serverAms 'release\ValheimAutoModSync.Client.dll')
    )

    foreach ($payload in $payloads) {
        if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) { continue }
        $payloadHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash.ToLowerInvariant()
        if ($payloadHash -ne $installedHash) {
            throw ("Installed AutoModSync client does not match the server-distributed client payload." + [Environment]::NewLine +
                "  Installed: " + $installed[0].FullName + [Environment]::NewLine +
                "  Server:    " + $payload + [Environment]::NewLine +
                "Run build-dev.bat, then deploy-dev.ps1 with BOTH -ClientBepInEx and -ServerBepInEx before this live gate.")
        }
    }

    Write-Host ('Verified client self-update parity: ' + $installedHash)
}

switch ($Action) {
    'Prepare' {
        Require-Clean-Apply-State
        Require-Matching-Client-Payloads
        if (Test-Path -LiteralPath $statePath -PathType Leaf) { throw "A Phase 7 live state already exists: $statePath" }
        if (Test-Path -LiteralPath $serverFixture -PathType Leaf) { throw "Phase 7 server fixture already exists: $serverFixture" }
        if (Test-Path -LiteralPath $clientFixture -PathType Leaf) { throw "Phase 7 client fixture already exists; do not delete an owned client fixture manually: $clientFixture" }

        Write-Host 'Creating one 128 MiB incompressible-ish inert client-only payload...'
        Write-Fixture
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $serverFixture).Hash.ToLowerInvariant()
        if ((Get-Item -LiteralPath $serverFixture).Length -ne $fixtureBytes) { throw 'Fixture length validation failed.' }

        [ordered]@{
            fixtureBytes = $fixtureBytes
            fixtureSha256 = $hash
            applyLogLines = (Line-Count $applyLog)
            preparedUtc = [DateTime]::UtcNow.ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

        Write-Host ''
        Write-Host 'ARMED: Phase 7 real production-event UI/telemetry gate.' -ForegroundColor Yellow
        Write-Host ('Server fixture: ' + $serverFixture)
        Write-Host ('SHA-256: ' + $hash)
        Write-Host ''
        Write-Host '1. Start the dedicated server.'
        Write-Host '2. Start Valheim and join the server once.'
        Write-Host '3. During the real download, capture the Downloading screen if convenient.'
        Write-Host '4. Allow AutoModSync to verify, apply, restart Valheim, and reconnect automatically.'
        Write-Host '5. Wait until the reconnect finishes and the server join proceeds normally.'
        Write-Host '6. Run this script with -Action Inspect.'
        Write-Host ''
        Write-Host 'Expected UI path: Comparing -> Downloading -> Verifying -> Applying -> Restarting -> Reconnecting -> Already synchronized.'
    }

    'Inspect' {
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'No prepared Phase 7 live state exists. Run -Action Prepare first.' }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $ok = $true

        Write-Host ''
        Write-Host 'Inspecting Phase 7 real-event transfer/apply/reconnect gate...'

        if (-not (Test-Path -LiteralPath $clientFixture -PathType Leaf)) {
            Write-Host '  FAIL client fixture was not installed.'
            $ok = $false
        }
        else {
            $clientItem = Get-Item -LiteralPath $clientFixture
            $clientHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $clientFixture).Hash.ToLowerInvariant()
            if ($clientItem.Length -eq [Int64]$state.fixtureBytes -and $clientHash -eq [string]$state.fixtureSha256) {
                Write-Host '  PASS synchronized 128 MiB fixture matches the exact server SHA-256.'
            }
            else {
                Write-Host '  FAIL synchronized fixture size/hash differs from the prepared server payload.'
                $ok = $false
            }
        }

        $newApply = @(New-Lines $applyLog ([int]$state.applyLogLines))
        $applyText = $newApply -join [Environment]::NewLine
        if ($applyText -match 'Transaction PREPARED' -and
            $applyText -match 'Applied .*__AMS_PHASE7_UI__[\\/]phase7-telemetry\.bin' -and
            $applyText -match 'Transaction COMMITTED' -and
            $applyText -match 'Committed transaction cleanup complete') {
            Write-Host '  PASS Apply helper reached PREPARED -> fixture write -> COMMITTED -> cleanup.'
        }
        else {
            Write-Host '  FAIL expected transactional Apply-helper evidence is incomplete.'
            $ok = $false
        }

        $serverText = (@(Read-SharedLines $serverLog) -join [Environment]::NewLine)
        if ($serverText -match 'AutoModSync bundle ready: cache=' -and
            $serverText -match 'AutoModSync scheduler transfer complete: rawPayload=') {
            $transferMatches = [regex]::Matches($serverText,'AutoModSync scheduler transfer complete: rawPayload=([^,]+), elapsed=([0-9.]+) s, avgRawPayload=([0-9.]+) MiB/s')
            if ($transferMatches.Count -gt 0) {
                $m = $transferMatches[$transferMatches.Count - 1]
                Write-Host ('  PASS server completed scheduled payload transfer: raw={0}, elapsed={1}s, avg={2} MiB/s.' -f $m.Groups[1].Value,$m.Groups[2].Value,$m.Groups[3].Value)
            } else {
                Write-Host '  PASS server logged bundle publication and scheduled transfer completion.'
            }
        }
        else {
            Write-Host '  FAIL missing current server bundle-ready/transfer-complete evidence.'
            $ok = $false
        }

        $clientText = (@(Read-SharedLines $clientLog) -join [Environment]::NewLine)
        if ($clientText -match 'AutoModSync queued one-shot in-game reconnect' -and
            $clientText -match 'AutoModSync: client mods already match the trusted server' -and
            $clientText -match 'AutoModSync released the original Valheim ServerHandshake') {
            Write-Host '  PASS restarted client performed one-shot reconnect and completed zero-delta trusted preflight.'
        }
        else {
            Write-Host '  FAIL current client log does not contain the expected post-restart reconnect/zero-delta evidence.'
            $ok = $false
        }

        $owned = @(Read-Phase7Ownership)
        if ($owned.Count -eq 1 -and $owned[0].Size -eq [Int64]$state.fixtureBytes -and $owned[0].Sha256 -eq [string]$state.fixtureSha256) {
            Write-Host '  PASS trusted-server ownership records exactly the Phase 7 fixture installed by AMS.'
        }
        else {
            Write-Host ('  FAIL expected exactly one Phase 7 ownership row; observed ' + $owned.Count + '.')
            $ok = $false
        }

        if ($ok) {
            Write-Host ''
            Write-Host 'PASS: real Phase 7 production events completed transfer, verification/apply, restart, automatic reconnect, and zero-delta follow-up.'
            Write-Host 'If the real Downloading screen looked correct, the next step is -Action PrepareCleanup.'
            return
        }

        Write-Host ''
        Write-Host 'Recent new Apply-helper lines:'
        $newApply | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        Write-Host ''
        Write-Host 'Recent server AutoModSync lines:'
        @(Read-SharedLines $serverLog) | Where-Object { $_ -match 'AutoModSync' } | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }
        Write-Host ''
        Write-Host 'Recent client AutoModSync lines:'
        @(Read-SharedLines $clientLog) | Where-Object { $_ -match 'AutoModSync' } | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }
        exit 1
    }

    'PrepareCleanup' {
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'No Phase 7 live state exists.' }
        Require-Clean-Apply-State
        Require-Matching-Client-Payloads
        if (-not (Test-Path -LiteralPath $clientFixture -PathType Leaf)) { throw 'Client fixture is already absent; cleanup reconciliation cannot be validated.' }
        if (Test-Path -LiteralPath $serverFixture -PathType Leaf) { Remove-Item -LiteralPath $serverFixture -Force }
        if ((Test-Path -LiteralPath $serverTestRoot -PathType Container) -and (@(Get-ChildItem -LiteralPath $serverTestRoot -Force).Count -eq 0)) {
            Remove-Item -LiteralPath $serverTestRoot -Force
        }

        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $state | Add-Member -NotePropertyName cleanupApplyLogLines -NotePropertyValue (Line-Count $applyLog) -Force
        $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

        Write-Host ''
        Write-Host 'Prepared Phase 7 fixture retirement.' -ForegroundColor Yellow
        Write-Host '1. Stop/restart the dedicated server so its signed manifest omits the fixture.'
        Write-Host '2. Join once and allow the delete-only AMS transaction, restart, and automatic reconnect.'
        Write-Host '3. Run -Action InspectCleanup.'
    }

    'InspectCleanup' {
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'No Phase 7 live state exists.' }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if (-not ($state.PSObject.Properties.Name -contains 'cleanupApplyLogLines')) { throw 'Cleanup was not prepared. Run -Action PrepareCleanup first.' }
        $ok = $true

        if (Test-Path -LiteralPath $serverFixture -PathType Leaf) {
            Write-Host '  FAIL server fixture still exists.'
            $ok = $false
        } else { Write-Host '  PASS server fixture is absent from the new manifest source.' }

        if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
            Write-Host '  FAIL owned client fixture was not retired.'
            $ok = $false
        } else { Write-Host '  PASS owned client fixture was retired transactionally.' }

        $owned = @(Read-Phase7Ownership)
        if ($owned.Count -eq 0) { Write-Host '  PASS Phase 7 fixture ownership row was removed.' }
        else {
            Write-Host ('  FAIL Phase 7 ownership still contains ' + $owned.Count + ' row(s).')
            $ok = $false
        }

        $newApply = @(New-Lines $applyLog ([int]$state.cleanupApplyLogLines))
        $applyText = $newApply -join [Environment]::NewLine
        if ($applyText -match 'Transaction PREPARED' -and
            $applyText -match 'Applied .*delete P:__AMS_PHASE7_UI__[\\/]phase7-telemetry\.bin' -and
            $applyText -match 'Transaction COMMITTED') {
            Write-Host '  PASS cleanup used the normal PREPARED/COMMITTED owned-delete transaction.'
        }
        else {
            Write-Host '  FAIL cleanup transaction evidence is incomplete.'
            $ok = $false
        }

        $clientText = (@(Read-SharedLines $clientLog) -join [Environment]::NewLine)
        if ($clientText -match 'AutoModSync: client mods already match the trusted server' -and
            $clientText -match 'AutoModSync released the original Valheim ServerHandshake') {
            Write-Host '  PASS cleanup restart/reconnect returned to a zero-delta trusted join.'
        }
        else {
            Write-Host '  FAIL post-cleanup zero-delta reconnect evidence is missing.'
            $ok = $false
        }

        if ($ok) {
            try { Remove-Item -LiteralPath $statePath -Force } catch {}
            $clientDir = Split-Path -Parent $clientFixture
            if ((Test-Path -LiteralPath $clientDir -PathType Container) -and (@(Get-ChildItem -LiteralPath $clientDir -Force).Count -eq 0)) {
                try { Remove-Item -LiteralPath $clientDir -Force } catch {}
            }
            Write-Host ''
            Write-Host 'PASS: Phase 7 live fixture was retired cleanly; no test payload or ownership row remains.'
            return
        }

        exit 1
    }
}
