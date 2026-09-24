param(
    [string]$Helper = (Join-Path $PSScriptRoot 'DevBuild\ValheimAutoModSync.Apply.exe'),
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 2 adversarial helper validation.
# This harness uses an isolated temporary BepInEx tree. It never touches the user's real Valheim install,
# server, trust files, or release artifacts. It requires the development Apply helper built by build-dev.bat.

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$crlf = [Environment]::NewLine
$root = Join-Path $env:TEMP ('AMS26-Phase2-Adversarial-' + $PID)
$gameRoot = Join-Path $root 'Game'
$bepInExRoot = Join-Path $gameRoot 'BepInEx'
$amsRoot = Join-Path $bepInExRoot 'AutoModSync'
$pluginRoot = Join-Path $bepInExRoot 'plugins'
$patcherRoot = Join-Path $bepInExRoot 'patchers'
$configRoot = Join-Path $bepInExRoot 'config'
$stagingRoot = Join-Path $amsRoot 'staging'
$txRoot = Join-Path $amsRoot 'apply-transaction'
$logPath = Join-Path $amsRoot 'apply.log'
$errorPath = Join-Path $amsRoot 'apply-error.txt'
$pendingPath = Join-Path $amsRoot 'pending.txt'

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Text, $script:utf8NoBom)
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Contains([string]$Path, [string]$Needle, [string]$Message) {
    Assert-True (Test-Path -LiteralPath $Path) ($Message + ' (file missing)')
    $text = [System.IO.File]::ReadAllText($Path)
    Assert-True ($text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) ($Message + " (missing text: $Needle)")
}

function Reset-Sandbox {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }

    @(
        $amsRoot,
        $pluginRoot,
        $patcherRoot,
        $configRoot,
        (Join-Path $stagingRoot 'plugins'),
        (Join-Path $stagingRoot 'patchers'),
        (Join-Path $stagingRoot 'config')
    ) | ForEach-Object {
        New-Item -ItemType Directory -Path $_ -Force | Out-Null
    }
}

function Get-KindDirectory([char]$Kind) {
    if ($Kind -eq 'P') { return 'plugins' }
    if ($Kind -eq 'R') { return 'patchers' }
    if ($Kind -eq 'C') { return 'config' }
    throw "Unsupported fixture kind: $Kind"
}

function Get-LiveRoot([char]$Kind) {
    if ($Kind -eq 'P') { return $pluginRoot }
    if ($Kind -eq 'R') { return $patcherRoot }
    if ($Kind -eq 'C') { return $configRoot }
    throw "Unsupported fixture kind: $Kind"
}

function Add-Fixture([char]$Kind, [string]$RelativePath, [string]$OldText, [string]$NewText) {
    $live = Join-Path (Get-LiveRoot $Kind) $RelativePath
    $stage = Join-Path (Join-Path $stagingRoot (Get-KindDirectory $Kind)) ($RelativePath + '.amsnew')
    Write-Utf8NoBom $live $OldText
    Write-Utf8NoBom $stage $NewText
    return [pscustomobject]@{
        Kind = $Kind
        RelativePath = $RelativePath
        Live = $live
        Stage = $stage
        OldText = $OldText
        NewText = $NewText
    }
}

function Write-Pending($Fixtures) {
    $lines = @()
    foreach ($f in $Fixtures) {
        $normalized = $f.RelativePath.Replace('\', '/')
        $lines += ($f.Kind.ToString() + ':' + $normalized)
    }
    Write-Utf8NoBom $pendingPath (($lines -join $crlf) + $crlf)
}

function Start-ApplyHelper {
    return Start-Process -FilePath $Helper -ArgumentList @('0', ('"{0}"' -f $amsRoot)) -WorkingDirectory (Split-Path -Parent $Helper) -WindowStyle Hidden -PassThru
}

function Invoke-ApplyHelper {
    $p = Start-ApplyHelper
    $p.WaitForExit()
    return $p.ExitCode
}

function Wait-ForLog([string]$Needle, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $logPath) {
            $text = [System.IO.File]::ReadAllText($logPath)
            if ($text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for apply.log text: $Needle"
}

function Start-And-KillAtPrepared {
    Write-Utf8NoBom (Join-Path $amsRoot 'apply-test-pause-after-prepared.once') ''
    $p = Start-ApplyHelper
    try {
        Wait-ForLog 'DEV TEST PAUSE after PREPARED, before first live write'
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit()
    }
    finally {
        if (-not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Replace-ManifestPath([string]$RelativePath) {
    $manifest = Join-Path $txRoot 'manifest.txt'
    $lines = [System.IO.File]::ReadAllLines($manifest)
    Assert-True ($lines.Length -ge 2) 'Transaction manifest did not contain an entry.'
    $parts = $lines[1].Split('|')
    Assert-True ($parts.Length -eq 8) 'Transaction manifest entry did not have eight fields.'
    $parts[7] = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($RelativePath))
    $lines[1] = ($parts -join '|')
    [System.IO.File]::WriteAllText($manifest, (($lines -join $crlf) + $crlf), $utf8NoBom)
}

function Restore-Manifest([byte[]]$Bytes) {
    [System.IO.File]::WriteAllBytes((Join-Path $txRoot 'manifest.txt'), $Bytes)
}

if (-not (Test-Path -LiteralPath $Helper)) {
    throw "Development Apply helper not found: $Helper. Run .\build-dev.bat first."
}

Write-Host 'AutoModSync 2.6 Phase 2 adversarial validation'
Write-Host "Helper:  $Helper"
Write-Host "Sandbox: $root"
Write-Host ''

try {
    # Test 1: a caught failure after a real live write must synchronously roll the entire PREPARED set back.
    Write-Host '[1/4] Caught per-file failure rollback...'
    Reset-Sandbox
    $a = Add-Fixture 'P' 'Phase2Fixture\a.txt' 'OLD-A' 'NEW-A'
    $b = Add-Fixture 'P' 'Phase2Fixture\b.txt' 'OLD-B' 'NEW-B'
    Write-Pending @($a, $b)
    Write-Utf8NoBom (Join-Path $amsRoot 'apply-test-fail-after-items.once') '1'

    $exitCode = Invoke-ApplyHelper
    Assert-True ($exitCode -eq 1) "Injected caught failure returned unexpected exit code $exitCode."
    Assert-True (([System.IO.File]::ReadAllText($a.Live)) -eq 'OLD-A') 'Fixture A was not rolled back to its exact old content.'
    Assert-True (([System.IO.File]::ReadAllText($b.Live)) -eq 'OLD-B') 'Fixture B was not preserved as its exact old content.'
    Assert-True (-not (Test-Path -LiteralPath $txRoot)) 'Transaction directory remained after caught-failure rollback.'
    Assert-True (Test-Path -LiteralPath $pendingPath) 'Pending staging was unexpectedly removed after caught-failure rollback.'
    Assert-Contains $logPath 'DEV TEST injected caught apply failure after 1 applied file(s).' 'Caught failure injection was not reached.'
    Assert-Contains $logPath 'Rollback complete; staged new files remain available for a clean retry.' 'Caught failure did not complete rollback.'
    Write-Host '  PASS'

    # Test 2: a version-mismatched journal must be rejected without guessing or touching the old live file.
    Write-Host '[2/4] Malformed/version-mismatched journal rejection...'
    Reset-Sandbox
    $a = Add-Fixture 'P' 'Phase2Fixture\version.txt' 'OLD-VERSION' 'NEW-VERSION'
    Write-Pending @($a)
    Start-And-KillAtPrepared

    $manifest = Join-Path $txRoot 'manifest.txt'
    $goodManifest = [System.IO.File]::ReadAllBytes($manifest)
    $manifestText = [System.Text.Encoding]::UTF8.GetString($goodManifest)
    Assert-True ($manifestText.StartsWith('AMSTXN2')) 'Prepared manifest did not use AMSTXN2.'
    Write-Utf8NoBom $manifest ($manifestText.Replace('AMSTXN2', 'AMSTXN999'))

    $exitCode = Invoke-ApplyHelper
    Assert-True ($exitCode -eq 1) "Version-mismatched journal returned unexpected exit code $exitCode."
    Assert-True (([System.IO.File]::ReadAllText($a.Live)) -eq 'OLD-VERSION') 'Malformed-journal recovery changed the live file.'
    Assert-True (Test-Path -LiteralPath $txRoot) 'Malformed journal was discarded instead of being left for operator recovery.'
    Assert-Contains $errorPath 'transaction manifest version/header is invalid' 'Version-mismatched journal was not rejected explicitly.'

    Restore-Manifest $goodManifest
    $null = Invoke-ApplyHelper
    Assert-True (([System.IO.File]::ReadAllText($a.Live)) -eq 'NEW-VERSION') 'Corrected journal did not recover and retry the staged new file.'
    Assert-True (-not (Test-Path -LiteralPath $txRoot)) 'Recovered version-journal transaction was not cleaned up.'
    Assert-True (-not (Test-Path -LiteralPath $pendingPath)) 'Recovered version-journal pending file was not cleaned up.'
    Write-Host '  PASS'

    # Test 3: recovery metadata cannot select a protected config or escape its fixed root.
    Write-Host '[3/4] Protected-config and fixed-root journal rejection...'
    Reset-Sandbox
    $a = Add-Fixture 'C' 'Phase2Fixture\safe.cfg' 'OLD-CONFIG' 'NEW-CONFIG'
    Write-Pending @($a)
    Start-And-KillAtPrepared
    $manifest = Join-Path $txRoot 'manifest.txt'
    $goodManifest = [System.IO.File]::ReadAllBytes($manifest)

    Replace-ManifestPath 'BepInEx.cfg'
    $exitCode = Invoke-ApplyHelper
    Assert-True ($exitCode -eq 1) "Protected-config journal returned unexpected exit code $exitCode."
    Assert-True (([System.IO.File]::ReadAllText($a.Live)) -eq 'OLD-CONFIG') 'Protected-config rejection changed the legitimate live file.'
    Assert-Contains $errorPath 'protected BepInEx/AutoModSync config path' 'Protected config path was not rejected.'

    Restore-Manifest $goodManifest
    Replace-ManifestPath '..\outside.txt'
    $exitCode = Invoke-ApplyHelper
    Assert-True ($exitCode -eq 1) "Escaping journal path returned unexpected exit code $exitCode."
    Assert-True (([System.IO.File]::ReadAllText($a.Live)) -eq 'OLD-CONFIG') 'Fixed-root rejection changed the legitimate live file.'
    Assert-Contains $errorPath 'Unsafe AutoModSync transaction path' 'Escaping transaction path was not rejected.'

    Restore-Manifest $goodManifest
    Write-Host '  PASS'

    # Test 4: recovery must reject a junction/reparse point at the transaction boundary.
    Write-Host '[4/4] Recovery reparse-point rejection...'
    $txReal = Join-Path $amsRoot 'apply-transaction-real'
    Rename-Item -LiteralPath $txRoot -NewName (Split-Path -Leaf $txReal)
    $mklink = 'mklink /J "{0}" "{1}"' -f $txRoot, $txReal
    & $env:ComSpec /d /c $mklink | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Could not create the test transaction junction.'

    $exitCode = Invoke-ApplyHelper
    Assert-True ($exitCode -eq 1) "Reparse-point recovery returned unexpected exit code $exitCode."
    Assert-Contains $errorPath 'refuses to traverse a filesystem reparse point' 'Transaction reparse point was not rejected.'

    $rmdir = 'rmdir "{0}"' -f $txRoot
    & $env:ComSpec /d /c $rmdir | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Could not remove the test transaction junction.'
    Rename-Item -LiteralPath $txReal -NewName 'apply-transaction'

    $null = Invoke-ApplyHelper
    Assert-True (([System.IO.File]::ReadAllText($a.Live)) -eq 'NEW-CONFIG') 'Restored safe transaction did not recover and apply normally.'
    Assert-True (-not (Test-Path -LiteralPath $txRoot)) 'Safe recovery did not clean up the transaction directory.'
    Assert-True (-not (Test-Path -LiteralPath $pendingPath)) 'Safe recovery did not clean up pending.txt.'
    Write-Host '  PASS'

    Write-Host ''
    Write-Host 'PASS: all Phase 2 adversarial helper checks passed.'
    Write-Host 'No real Valheim installation or server files were modified.'

    if (-not $KeepSandbox) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    else {
        Write-Host "Sandbox retained: $root"
    }
}
catch {
    Write-Host ''
    Write-Host ('FAIL: ' + $_.Exception.Message)
    Write-Host "Sandbox retained for inspection: $root"
    exit 1
}
