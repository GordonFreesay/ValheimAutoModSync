param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Arm','Inspect','CleanupState')]
    [string]$Action,

    [Parameter(Mandatory=$true)]
    [string]$ServerBepInEx,

    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx,

    [int]$DisconnectAfterChunks = 2048
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 7 interrupted-transfer/resume live gate.
# Intent: arms the existing development-only exact-chunk disconnect hook against the bounded Phase 7 inert fixture,
# then verifies that both peers preserved/accepted the same immutable bundle prefix before the normal apply/restart/reconnect path completed.
# Safety: this script never edits live synchronized destinations; the Phase 7 fixture remains under __AMS_PHASE7_UI__ and is retired through the normal ownership cleanup gate.

$serverRoot = [IO.Path]::GetFullPath($ServerBepInEx).TrimEnd('\','/')
$clientRoot = [IO.Path]::GetFullPath($ClientBepInEx).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $serverRoot -PathType Container)) { throw "Server BepInEx root not found: $serverRoot" }
if (-not (Test-Path -LiteralPath $clientRoot -PathType Container)) { throw "Client BepInEx root not found: $clientRoot" }

$serverFixture = Join-Path $serverRoot 'AutoModSync\ClientPayload\plugins\__AMS_PHASE7_UI__\phase7-telemetry.bin'
$clientFixture = Join-Path $clientRoot 'plugins\__AMS_PHASE7_UI__\phase7-telemetry.bin'
$clientAms = Join-Path $clientRoot 'AutoModSync'
$marker = Join-Path $clientAms 'resume-test-disconnect-after-chunks.once'
$clientLog = Join-Path $clientRoot 'LogOutput.log'
$serverLog = Join-Path $serverRoot 'LogOutput.log'
$statePath = Join-Path $env:TEMP 'AMS26-Phase7-Resume-State.json'

function Read-SharedLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true,4096,$false)
    try {
        $list = New-Object 'System.Collections.Generic.List[string]'
        while (-not $reader.EndOfStream) { [void]$list.Add($reader.ReadLine()) }
        return @($list.ToArray())
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function New-Lines([string]$Path,[int]$Start) {
    $all = @(Read-SharedLines $Path)
    if ($Start -lt 0) { $Start = 0 }
    # A Valheim/dedicated-server restart replaces LogOutput.log. If the new file is shorter
    # than the armed baseline, treat the entire current file as post-arm evidence instead
    # of indexing past the end of a different process's log.
    if ($all.Count -lt $Start) { return $all }
    if ($all.Count -eq $Start) { return @() }
    return @($all[$Start..($all.Count-1)])
}

switch ($Action) {
    'Arm' {
        if (-not (Test-Path -LiteralPath $serverFixture -PathType Leaf)) {
            throw 'Phase 7 fixture is not present on the server. Run test-phase7-live.ps1 -Action Prepare first.'
        }
        if (Test-Path -LiteralPath $clientFixture -PathType Leaf) {
            throw 'Phase 7 fixture is already installed on the client. Retire it through the normal Phase 7 cleanup gate before arming a fresh resume test.'
        }
        if ($DisconnectAfterChunks -lt 1) { throw 'DisconnectAfterChunks must be at least 1.' }

        New-Item -ItemType Directory -Path $clientAms -Force | Out-Null
        [IO.File]::WriteAllText($marker,$DisconnectAfterChunks.ToString([Globalization.CultureInfo]::InvariantCulture),[Text.UTF8Encoding]::new($false))

        [pscustomobject]@{
            armedUtc = [DateTime]::UtcNow.ToString('o')
            disconnectAfterChunks = $DisconnectAfterChunks
            clientLogLines = @(Read-SharedLines $clientLog).Count
            serverLogLines = @(Read-SharedLines $serverLog).Count
            serverSha256 = (Get-FileHash -LiteralPath $serverFixture -Algorithm SHA256).Hash.ToLowerInvariant()
        } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

        Write-Host ('ARMED: disconnect after complete chunk ' + $DisconnectAfterChunks + '.') -ForegroundColor Yellow
        Write-Host ''
        Write-Host '1. Restart/start the dedicated server so the prepared Phase 7 fixture is in its signed manifest.'
        Write-Host '2. Start Valheim and join the AMS server.'
        Write-Host '3. The development client will intentionally close the transfer socket mid-download.'
        Write-Host '4. Observe the branded FAILED screen: it should say verified package data was retained, then disappear by itself after about 5 seconds.'
        Write-Host '5. Manually join the same server again.'
        Write-Host '6. Observe the Downloading screen: it should say RESUMED and show retained bytes; CURRENT/AVERAGE/ETA should describe this resumed session rather than counting retained bytes as newly transferred.'
        Write-Host '7. Let verify/apply/restart/automatic reconnect finish completely.'
        Write-Host '8. Run this script with -Action Inspect, then run test-phase7-live.ps1 -Action Inspect for the normal final transaction/reconnect evidence.'
    }

    'Inspect' {
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'No armed resume state exists. Run -Action Arm first.' }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $clientNew = @(New-Lines $clientLog ([int]$state.clientLogLines))
        # The guided sequence deliberately starts/restarts the dedicated server after Arm, so its
        # LogOutput.log is a new process file. Search that current process log in full for the exact
        # armed resume boundary rather than applying a line index captured from the prior process.
        $serverNew = @(Read-SharedLines $serverLog)
        $clientText = $clientNew -join [Environment]::NewLine
        $serverText = $serverNew -join [Environment]::NewLine

        $fail = $false
        $serverResume = [regex]::Match($serverText,'AutoModSync exact-artifact resume accepted at chunk ([0-9]+)/([0-9]+) \(([^,]+) retained, prefixVerify=([0-9.]+) s\)')
        $serverResumeChunk = 0
        if ($serverResume.Success) { $serverResumeChunk = [int]$serverResume.Groups[1].Value }

        if ($clientText -match 'DEV TEST closing the transfer socket after chunk ([0-9]+)') {
            Write-Host ('PASS client forced one deterministic transfer interruption after chunk ' + $Matches[1] + '.')
        }
        elseif (-not (Test-Path -LiteralPath $marker -PathType Leaf) -and $serverResumeChunk -eq [int]$state.disconnectAfterChunks) {
            Write-Host ('PASS one-shot interruption marker was consumed and the server later accepted the exact armed chunk boundary ' + $serverResumeChunk + '.')
            Write-Host '  INFO client pre-restart interruption log was replaced by the final Valheim restart.'
        }
        else { Write-Host 'FAIL forced transfer interruption evidence is missing.'; $fail = $true }

        if ($clientText -match 'Preserved ([0-9.]+ [A-Za-z]+) of the verified bundle prefix') {
            Write-Host ('PASS client preserved interrupted verified prefix: ' + $Matches[1] + '.')
        }
        elseif ($serverResumeChunk -gt 0) {
            Write-Host ('PASS preserved client prefix is proven by the server accepting the client resume candidate at nonzero chunk ' + $serverResumeChunk + '.')
            Write-Host '  INFO the client preservation log belonged to the pre-apply process and may no longer exist after automatic restart.'
        }
        else { Write-Host 'FAIL client preserved-prefix evidence is missing.'; $fail = $true }

        $clientResume = [regex]::Match($clientText,'AutoModSync exact-artifact resume accepted at chunk ([0-9]+)/([0-9]+) \(([^\)]+) retained\)')
        if ($clientResume.Success -and [int]$clientResume.Groups[1].Value -gt 0) {
            Write-Host ('PASS client reopened exact-artifact resume at chunk ' + $clientResume.Groups[1].Value + '/' + $clientResume.Groups[2].Value + ' with ' + $clientResume.Groups[3].Value + ' retained.')
        }
        elseif ($serverResumeChunk -gt 0) {
            Write-Host ('PASS client submitted a valid retained-prefix resume candidate accepted by the server at chunk ' + $serverResumeChunk + '.')
            Write-Host '  INFO the client acceptance log may have been replaced by the automatic post-apply Valheim restart.'
        }
        else { Write-Host 'FAIL exact-artifact resume acceptance evidence is missing.'; $fail = $true }

        if ($serverResume.Success -and $serverResumeChunk -gt 0) {
            Write-Host ('PASS server independently verified and accepted the prefix at chunk ' + $serverResume.Groups[1].Value + '/' + $serverResume.Groups[2].Value + ' with ' + $serverResume.Groups[3].Value + ' retained in ' + $serverResume.Groups[4].Value + ' s.')
            if ($serverResumeChunk -ne [int]$state.disconnectAfterChunks) {
                Write-Host ('FAIL server accepted chunk ' + $serverResumeChunk + ' but the armed deterministic boundary was ' + [int]$state.disconnectAfterChunks + '.')
                $fail = $true
            }
        } else { Write-Host 'FAIL server exact-artifact prefix-verification evidence is missing.'; $fail = $true }

        if ($clientResume.Success -and $serverResume.Success -and $clientResume.Groups[1].Value -ne $serverResume.Groups[1].Value) {
            Write-Host 'FAIL client/server accepted different resume chunk boundaries.'
            $fail = $true
        }

        $transfer = [regex]::Matches($serverText,'AutoModSync scheduler transfer complete: rawPayload=([^,]+), elapsed=([0-9.]+) s, avgRawPayload=([0-9.]+) MiB/s') | Select-Object -Last 1
        if ($transfer -ne $null) {
            Write-Host ('PASS resumed scheduled transfer completed: raw=' + $transfer.Groups[1].Value + ', elapsed=' + $transfer.Groups[2].Value + 's, avg=' + $transfer.Groups[3].Value + ' MiB/s.')
        }

        if (-not (Test-Path -LiteralPath $clientFixture -PathType Leaf)) {
            Write-Host 'FAIL resumed Phase 7 fixture is not installed on the client.'
            $fail = $true
        } else {
            $clientSha = (Get-FileHash -LiteralPath $clientFixture -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($clientSha -eq [string]$state.serverSha256) { Write-Host 'PASS resumed fixture matches the exact prepared server SHA-256.' }
            else { Write-Host 'FAIL resumed fixture does not match the prepared server SHA-256.'; $fail = $true }
        }

        if ($fail) { exit 1 }
        Write-Host ''
        Write-Host 'PASS: interrupted transfer preserved an exact verified prefix, resumed only after server prefix verification, and produced the exact final fixture.'
        Write-Host 'Visual confirmation of the 5-second failure cleanup and RESUMED telemetry remains required from the live UI.'
    }

    'CleanupState' {
        if (Test-Path -LiteralPath $marker -PathType Leaf) { Remove-Item -LiteralPath $marker -Force }
        if (Test-Path -LiteralPath $statePath -PathType Leaf) { Remove-Item -LiteralPath $statePath -Force }
        Write-Host 'Removed Phase 7 resume-test marker/state only. Retire the synchronized fixture through test-phase7-live.ps1 -Action PrepareCleanup / InspectCleanup.'
    }
}
