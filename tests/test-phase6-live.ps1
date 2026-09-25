param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('PrepareInitial','PrepareTransition','PrepareDeleteOnly','Inspect','Cleanup')]
    [string]$Action,

    [Parameter(Mandatory=$true)]
    [string]$ServerBepInEx,

    [Parameter(Mandatory=$true)]
    [string]$ClientBepInEx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 6 live lifecycle helper.
# Intent: prepare/inspect only a fixed harmless test subtree. The operator still controls server restarts and joins.
# Safety: this script never touches files outside:
#   <server>\AutoModSync\ClientPayload\plugins\__AMS_PHASE6_TEST__
#   <client>\plugins\__AMS_PHASE6_TEST__
# It does not modify trust, configuration, real mod DLLs, ownership metadata, or release artifacts.

$serverRoot = [IO.Path]::GetFullPath($ServerBepInEx).TrimEnd('\','/')
$clientRoot = [IO.Path]::GetFullPath($ClientBepInEx).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $serverRoot -PathType Container)) { throw "Server BepInEx root not found: $serverRoot" }
if (-not (Test-Path -LiteralPath $clientRoot -PathType Container)) { throw "Client BepInEx root not found: $clientRoot" }

$serverTest = Join-Path $serverRoot 'AutoModSync\ClientPayload\plugins\__AMS_PHASE6_TEST__'
$clientTest = Join-Path $clientRoot 'plugins\__AMS_PHASE6_TEST__'
$clientAms = Join-Path $clientRoot 'AutoModSync'
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-TestFile([string]$Path,[string]$Text) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path,$Text,$utf8)
}

function Remove-TestTree([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}

function Read-TestOwnership {
    $ownershipRoot = Join-Path $clientAms 'ownership'
    $rows = @()
    if (-not (Test-Path -LiteralPath $ownershipRoot -PathType Container)) { return $rows }

    Get-ChildItem -LiteralPath $ownershipRoot -Filter '*.txt' -File | ForEach-Object {
        $lines = @(Get-Content -LiteralPath $_.FullName)
        if ($lines.Count -lt 2 -or $lines[0] -ne 'AMSOWN1') { return }
        $fingerprint = $lines[1].Trim()
        foreach ($line in $lines | Select-Object -Skip 2) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $parts = $line -split '\|',4
            if ($parts.Count -ne 4) { continue }
            try { $rel = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[3])) } catch { continue }
            if ($rel -like '__AMS_PHASE6_TEST__\*' -or $rel -like '__AMS_PHASE6_TEST__/*') {
                $rows += [pscustomobject]@{
                    Fingerprint = $fingerprint
                    Kind = $parts[0]
                    Size = [Int64]$parts[1]
                    Sha256 = $parts[2]
                    Path = $rel
                }
            }
        }
    }
    return $rows
}

switch ($Action) {
    'PrepareInitial' {
        Remove-TestTree $serverTest
        Remove-TestTree $clientTest
        New-Item -ItemType Directory -Path $serverTest -Force | Out-Null
        New-Item -ItemType Directory -Path $clientTest -Force | Out-Null

        Write-TestFile (Join-Path $serverTest 'owned.txt') 'phase6-owned-v1'
        Write-TestFile (Join-Path $serverTest 'modified.txt') 'phase6-modified-v1'
        Write-TestFile (Join-Path $serverTest 'preexisting.txt') 'phase6-preexisting-v1'

        # Exact matching local bytes exist before the first reconciliation. AMS must not claim them.
        Copy-Item -LiteralPath (Join-Path $serverTest 'preexisting.txt') -Destination (Join-Path $clientTest 'preexisting.txt') -Force

        Write-Host 'Prepared Phase 6 initial state.'
        Write-Host 'Expected first join: owned.txt + modified.txt are installed/owned; preexisting.txt already matches and is not claimed.'
        Write-Host 'Restart the dedicated server, join once, allow AMS apply/restart/reconnect, then run -Action Inspect.'
    }

    'PrepareTransition' {
        if (-not (Test-Path -LiteralPath (Join-Path $clientTest 'owned.txt') -PathType Leaf)) { throw 'Client owned.txt is missing; first join did not install the acquisition fixture.' }
        if (-not (Test-Path -LiteralPath (Join-Path $clientTest 'modified.txt') -PathType Leaf)) { throw 'Client modified.txt is missing; first join did not install the modification fixture.' }
        if (-not (Test-Path -LiteralPath (Join-Path $clientTest 'preexisting.txt') -PathType Leaf)) { throw 'Client preexisting.txt is missing.' }

        [IO.File]::AppendAllText((Join-Path $clientTest 'modified.txt'),'-LOCAL-MODIFIED',$utf8)

        if (Test-Path -LiteralPath (Join-Path $serverTest 'owned.txt')) {
            Move-Item -LiteralPath (Join-Path $serverTest 'owned.txt') -Destination (Join-Path $serverTest 'renamed.txt') -Force
        } elseif (-not (Test-Path -LiteralPath (Join-Path $serverTest 'renamed.txt'))) {
            throw 'Server owned.txt/renamed.txt fixture is missing.'
        }

        Remove-Item -LiteralPath (Join-Path $serverTest 'modified.txt') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $serverTest 'preexisting.txt') -Force -ErrorAction SilentlyContinue

        Write-Host 'Prepared Phase 6 transition state.'
        Write-Host 'Expected next join: owned.txt deleted + renamed.txt installed transactionally; modified.txt preserved/relinquished; preexisting.txt preserved and still unowned.'
        Write-Host 'Restart the dedicated server, join once, allow AMS apply/restart/reconnect, then run -Action Inspect.'
    }

    'PrepareDeleteOnly' {
        Remove-Item -LiteralPath (Join-Path $serverTest 'renamed.txt') -Force -ErrorAction SilentlyContinue
        Write-Host 'Prepared Phase 6 delete-only state.'
        Write-Host 'Expected next join: renamed.txt is removed by a delete-only transaction; modified.txt and preexisting.txt remain.'
        Write-Host 'Restart the dedicated server, join once, allow AMS apply/restart/reconnect, then run -Action Inspect.'
    }

    'Inspect' {
        $names = @('owned.txt','renamed.txt','modified.txt','preexisting.txt')
        $rows = foreach ($name in $names) {
            $path = Join-Path $clientTest $name
            [pscustomobject]@{
                File = $name
                Exists = Test-Path -LiteralPath $path -PathType Leaf
                Length = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-Item -LiteralPath $path).Length } else { $null }
                Sha256 = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() } else { '' }
            }
        }
        $rows | Format-Table -AutoSize

        Write-Host ''
        Write-Host 'Phase 6 ownership rows for the test subtree:'
        $owned = @(Read-TestOwnership)
        if ($owned.Count -eq 0) {
            Write-Host '(none)'
        } else {
            $owned | Sort-Object Path | Format-Table -AutoSize
        }

        $prior = Join-Path $clientAms 'last-successful-server.txt'
        Write-Host ''
        if (Test-Path -LiteralPath $prior -PathType Leaf) {
            Write-Host ('Last successful server fingerprint: ' + ((Get-Content -LiteralPath $prior -Raw).Trim()))
        } else {
            Write-Host 'Last successful server fingerprint: (missing)'
        }

        $applyLog = Join-Path $clientAms 'apply.log'
        if (Test-Path -LiteralPath $applyLog -PathType Leaf) {
            Write-Host ''
            Write-Host 'Recent apply transaction lines:'
            Get-Content -LiteralPath $applyLog -Tail 40 | Where-Object {
                $_ -match 'Preparing transactional apply|PREPARED|Applied |COMMITTED|ownership|cleanup'
            }
        }
    }

    'Cleanup' {
        Remove-TestTree $serverTest
        Remove-TestTree $clientTest
        Write-Host 'Removed only the Phase 6 live-test server/client test subtrees.'
        Write-Host 'Ownership metadata was intentionally not edited; after a successful delete-only reconciliation it should already contain no test entries.'
    }
}
