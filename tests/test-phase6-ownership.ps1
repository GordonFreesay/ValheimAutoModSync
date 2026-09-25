param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 6 ownership/apply validation.
# Compiles the production Apply helper + ownership/path-safety sources and runs only inside an isolated temporary BepInEx tree.
# It never reads or modifies the user's real Valheim installation, trust files, server files, or release artifacts.

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'Source'
$applySource = Join-Path $sourceRoot 'ValheimAutoModSync.Apply.cs'
$pathSource = Join-Path $sourceRoot 'AutoModSync.PathSafety.cs'
$ownershipSource = Join-Path $sourceRoot 'AutoModSync.OwnershipState.cs'

foreach ($path in @($applySource, $pathSource, $ownershipSource)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing source: $path" }
}

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$crlf = [Environment]::NewLine
$sandbox = Join-Path $env:TEMP ('AMS26-Phase6-Ownership-' + $PID)
$helper = Join-Path $sandbox 'ValheimAutoModSync.Apply.exe'
$pathSmokeSource = Join-Path $sandbox 'PathSafetySmoke.cs'
$pathSmokeExe = Join-Path $sandbox 'PathSafetySmoke.exe'
$gameRoot = Join-Path $sandbox 'Game'
$bepInExRoot = Join-Path $gameRoot 'BepInEx'
$amsRoot = Join-Path $bepInExRoot 'AutoModSync'
$pluginRoot = Join-Path $bepInExRoot 'plugins'
$stagingRoot = Join-Path $amsRoot 'staging\plugins'
$pendingPath = Join-Path $amsRoot 'pending.txt'
$ownershipPending = Join-Path $amsRoot 'ownership-next.txt'
$ownershipRoot = Join-Path $amsRoot 'ownership'
$logPath = Join-Path $amsRoot 'apply.log'

New-Item -ItemType Directory -Path $sandbox -Force | Out-Null

& $csc /nologo /optimize+ /langversion:5 /target:winexe /define:AMS_DEV_TESTS /out:$helper $applySource $pathSource $ownershipSource
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 6 helper compilation failed. Sandbox retained: $sandbox"
    exit $LASTEXITCODE
}

$smokeCode = @'
using System;
using System.IO;
namespace ValheimAutoModSync
{
    internal static class PathSafetySmoke
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            AutoModSyncPathSafety.EnsureNoReparsePoints(root, root, true);
            return 0;
        }
    }
}
'@
[IO.File]::WriteAllText($pathSmokeSource, $smokeCode, $utf8NoBom)
& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$pathSmokeExe $pathSource $pathSmokeSource
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 6 exact-root path-safety smoke compilation failed. Sandbox retained: $sandbox"
    exit $LASTEXITCODE
}

function Get-ShaHex([string]$Text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose() }
}

function Write-Utf8([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, $script:utf8NoBom)
}

function B64([string]$Text) {
    return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Text))
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Reset-Sandbox {
    if (Test-Path -LiteralPath $gameRoot) { Remove-Item -LiteralPath $gameRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $amsRoot,$pluginRoot,$stagingRoot,$ownershipRoot -Force | Out-Null
    Write-Utf8 (Join-Path $amsRoot 'trusted-servers.txt') ($script:fpA + $crlf + $script:fpB + $crlf)
}

function Write-Ledger([string]$Fingerprint, $Entries) {
    $lines = @('AMSOWN1', $Fingerprint)
    foreach ($e in $Entries) {
        $lines += ($e.Kind + '|' + $e.Size + '|' + $e.Sha + '|' + (B64 $e.Rel))
    }
    Write-Utf8 (Join-Path $ownershipRoot ($Fingerprint + '.txt')) (($lines -join $crlf) + $crlf)
}

function Write-OwnershipPending([string]$Fingerprint, $Entries) {
    $lines = @('AMSOWN1', $Fingerprint)
    foreach ($e in $Entries) {
        $lines += ($e.Kind + '|' + $e.Size + '|' + $e.Sha + '|' + (B64 $e.Rel))
    }
    Write-Utf8 $ownershipPending (($lines -join $crlf) + $crlf)
}

function Write-PendingWrite([string]$Kind, [string]$Rel) {
    Write-Utf8 $pendingPath ('AMSPENDING2' + $crlf + 'W|' + $Kind + '|' + (B64 $Rel) + $crlf)
}

function Write-PendingDelete([string]$Kind, [string]$Rel, [long]$Size, [string]$Sha) {
    Write-Utf8 $pendingPath ('AMSPENDING2' + $crlf + 'D|' + $Kind + '|' + $Size + '|' + $Sha + '|' + (B64 $Rel) + $crlf)
}

function Invoke-Helper {
    $p = Start-Process -FilePath $helper -ArgumentList @('0', ('"{0}"' -f $amsRoot)) -WorkingDirectory $sandbox -WindowStyle Hidden -PassThru
    $p.WaitForExit()
    return $p.ExitCode
}

function Start-Helper {
    return Start-Process -FilePath $helper -ArgumentList @('0', ('"{0}"' -f $amsRoot)) -WorkingDirectory $sandbox -WindowStyle Hidden -PassThru
}

function Wait-ForLog([string]$Needle, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $logPath) {
            try {
                $text = [System.IO.File]::ReadAllText($logPath)
                if ($text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return }
            }
            catch [System.IO.IOException] {
                # The helper may have the log open for the exact line we are polling; retry instead of turning a timing race into a test failure.
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for apply.log text: $Needle"
}

function Stop-Helper($Process) {
    if ($Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        try { $Process.WaitForExit() } catch { }
    }
}

function Ownership-Entry([string]$Rel, [string]$Text) {
    return [pscustomobject]@{
        Kind = 'P'
        Rel  = $Rel
        Size = [System.Text.Encoding]::UTF8.GetByteCount($Text)
        Sha  = Get-ShaHex $Text
    }
}

$fpA = Get-ShaHex 'phase6-server-A'
$fpB = Get-ShaHex 'phase6-server-B'
$rel = 'Phase6Fixture\owned.txt'
$ownedText = 'SERVER-A-OWNED'
$owned = Ownership-Entry $rel $ownedText
$live = Join-Path $pluginRoot $rel
$staged = Join-Path $stagingRoot ($rel + '.amsnew')

Write-Host 'AutoModSync 2.6 Phase 6 ownership validation'
Write-Host "Sandbox: $sandbox"
Write-Host ''

try {
    Write-Host '[0/7] Exact trusted root is valid for reparse checking...'
    New-Item -ItemType Directory -Path $amsRoot -Force | Out-Null
    & $pathSmokeExe $amsRoot
    Assert-True ($LASTEXITCODE -eq 0) "Exact-root path-safety smoke test returned unexpected exit code $LASTEXITCODE."
    Write-Host '  PASS'

    Write-Host '[1/7] Verified write acquires ownership only after COMMITTED...'
    Reset-Sandbox
    Write-Utf8 $staged $ownedText
    Write-PendingWrite 'P' $rel
    Write-OwnershipPending $fpA @($owned)
    $exit = Invoke-Helper
    Assert-True ($exit -eq 3) "Successful isolated apply returned unexpected exit code $exit."
    Assert-True ((Test-Path -LiteralPath $live) -and ([IO.File]::ReadAllText($live) -eq $ownedText)) 'Verified write did not reach the live path.'
    $ledgerA = Join-Path $ownershipRoot ($fpA + '.txt')
    Assert-True (Test-Path -LiteralPath $ledgerA) 'Committed write did not publish server-A ownership.'
    Assert-True (([IO.File]::ReadAllText($ledgerA)).Contains($owned.Sha)) 'Published ownership ledger did not contain the installed digest.'
    Assert-True (-not (Test-Path -LiteralPath $pendingPath)) 'Committed write left pending.txt behind.'
    Assert-True (-not (Test-Path -LiteralPath $ownershipPending)) 'Committed write left ownership-next behind.'
    Write-Host '  PASS'

    Write-Host '[2/7] Exact same-server owned bytes can be retired transactionally...'
    Write-PendingDelete 'P' $rel $owned.Size $owned.Sha
    Write-OwnershipPending $fpA @()
    $exit = Invoke-Helper
    Assert-True ($exit -eq 3) "Successful isolated delete returned unexpected exit code $exit."
    Assert-True (-not (Test-Path -LiteralPath $live)) 'Exact owned stale file was not deleted.'
    $ledgerText = [IO.File]::ReadAllText($ledgerA)
    Assert-True (-not $ledgerText.Contains($owned.Sha)) 'Committed delete retained stale ownership.'
    Write-Host '  PASS'

    Write-Host '[3/7] Wrong digest deletion is rejected without touching the live file...'
    Reset-Sandbox
    Write-Utf8 $live $ownedText
    Write-Ledger $fpA @($owned)
    $wrongSha = Get-ShaHex 'NOT-THE-OWNED-BYTES'
    Write-PendingDelete 'P' $rel $owned.Size $wrongSha
    Write-OwnershipPending $fpA @()
    $exit = Invoke-Helper
    Assert-True ($exit -eq 1) "Wrong-digest deletion returned unexpected exit code $exit."
    Assert-True ((Test-Path -LiteralPath $live) -and ([IO.File]::ReadAllText($live) -eq $ownedText)) 'Wrong-digest deletion changed the live file.'
    Assert-True (([IO.File]::ReadAllText((Join-Path $ownershipRoot ($fpA + '.txt')))).Contains($owned.Sha)) 'Wrong-digest deletion changed ownership.'
    Write-Host '  PASS'

    Write-Host '[4/7] Another trusted server cannot delete server-A ownership...'
    Reset-Sandbox
    Write-Utf8 $live $ownedText
    Write-Ledger $fpA @($owned)
    Write-PendingDelete 'P' $rel $owned.Size $owned.Sha
    Write-OwnershipPending $fpB @()
    $exit = Invoke-Helper
    Assert-True ($exit -eq 1) "Cross-server deletion returned unexpected exit code $exit."
    Assert-True ((Test-Path -LiteralPath $live) -and ([IO.File]::ReadAllText($live) -eq $ownedText)) 'Cross-server deletion changed the live file.'
    Assert-True (([IO.File]::ReadAllText((Join-Path $ownershipRoot ($fpA + '.txt')))).Contains($owned.Sha)) 'Cross-server deletion changed server-A ownership.'
    Write-Host '  PASS'

    Write-Host '[5/7] PREPARED deletion interruption rolls the file back before retry...'
    Reset-Sandbox
    Write-Utf8 $live $ownedText
    Write-Ledger $fpA @($owned)
    Write-PendingDelete 'P' $rel $owned.Size $owned.Sha
    Write-OwnershipPending $fpA @()
    Write-Utf8 (Join-Path $amsRoot 'apply-test-pause-after-items.once') '1'
    $p = Start-Helper
    try {
        Wait-ForLog 'DEV TEST PAUSE after 1 applied file(s), before COMMITTED'
        Assert-True (-not (Test-Path -LiteralPath $live)) 'Injected PREPARED deletion did not remove the live file before pause.'
        Assert-True (([IO.File]::ReadAllText((Join-Path $ownershipRoot ($fpA + '.txt')))).Contains($owned.Sha)) 'Ownership changed before COMMITTED.'
        Stop-Helper $p
    }
    finally { Stop-Helper $p }

    Write-Utf8 (Join-Path $amsRoot 'apply-test-pause-after-rollback.once') ''
    $p = Start-Helper
    try {
        Wait-ForLog 'DEV TEST PAUSE after rollback, before retry'
        Assert-True ((Test-Path -LiteralPath $live) -and ([IO.File]::ReadAllText($live) -eq $ownedText)) 'Rollback did not restore the deleted owned file.'
        Assert-True (([IO.File]::ReadAllText((Join-Path $ownershipRoot ($fpA + '.txt')))).Contains($owned.Sha)) 'Rollback changed the old ownership ledger.'
        Stop-Helper $p
    }
    finally { Stop-Helper $p }

    $exit = Invoke-Helper
    Assert-True ($exit -eq 3) "Retry after rollback returned unexpected exit code $exit."
    Assert-True (-not (Test-Path -LiteralPath $live)) 'Retry after rollback did not commit the stale deletion.'
    Assert-True (-not ([IO.File]::ReadAllText((Join-Path $ownershipRoot ($fpA + '.txt'))).Contains($owned.Sha))) 'Retry after rollback did not publish new ownership.'
    Write-Host '  PASS'

    Write-Host '[6/7] COMMITTED interruption preserves new live state and publishes ownership during recovery...'
    Reset-Sandbox
    Write-Utf8 $staged $ownedText
    Write-PendingWrite 'P' $rel
    Write-OwnershipPending $fpA @($owned)
    Write-Utf8 (Join-Path $amsRoot 'apply-test-pause-after-committed.once') ''
    $p = Start-Helper
    try {
        Wait-ForLog 'DEV TEST PAUSE after COMMITTED'
        Assert-True ((Test-Path -LiteralPath $live) -and ([IO.File]::ReadAllText($live) -eq $ownedText)) 'COMMITTED write did not preserve the complete new live file.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $ownershipRoot ($fpA + '.txt')))) 'Ownership was published before committed cleanup/recovery.'
        Stop-Helper $p
    }
    finally { Stop-Helper $p }

    $exit = Invoke-Helper
    Assert-True ($exit -eq 3) "COMMITTED recovery returned unexpected exit code $exit."
    Assert-True ((Test-Path -LiteralPath $live) -and ([IO.File]::ReadAllText($live) -eq $ownedText)) 'COMMITTED recovery changed the winning live state.'
    Assert-True (([IO.File]::ReadAllText((Join-Path $ownershipRoot ($fpA + '.txt')))).Contains($owned.Sha)) 'COMMITTED recovery did not publish ownership.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $amsRoot 'apply-transaction'))) 'COMMITTED recovery did not clean the transaction directory.'
    Write-Host '  PASS'

    Write-Host '[7/7] Ownership deletion stays inside P/R/C roots and protected config remains forbidden...'
    Reset-Sandbox
    $patcherRoot = Join-Path $bepInExRoot 'patchers'
    $configRoot = Join-Path $bepInExRoot 'config'
    New-Item -ItemType Directory -Path $patcherRoot,$configRoot -Force | Out-Null

    $patchRel = 'Phase6Fixture\owned-patcher.txt'
    $configRel = 'Phase6Fixture\owned-client.cfg'
    $patchText = 'PATCHER-OWNED'
    $configText = 'CONFIG-OWNED'
    $patch = [pscustomobject]@{ Kind='R'; Rel=$patchRel; Size=[Text.Encoding]::UTF8.GetByteCount($patchText); Sha=Get-ShaHex $patchText }
    $config = [pscustomobject]@{ Kind='C'; Rel=$configRel; Size=[Text.Encoding]::UTF8.GetByteCount($configText); Sha=Get-ShaHex $configText }
    $patchLive = Join-Path $patcherRoot $patchRel
    $configLive = Join-Path $configRoot $configRel
    Write-Utf8 $patchLive $patchText
    Write-Utf8 $configLive $configText
    Write-Ledger $fpA @($patch,$config)

    Write-Utf8 $pendingPath ('AMSPENDING2' + $crlf +
        'D|R|' + $patch.Size + '|' + $patch.Sha + '|' + (B64 $patchRel) + $crlf +
        'D|C|' + $config.Size + '|' + $config.Sha + '|' + (B64 $configRel) + $crlf)
    Write-OwnershipPending $fpA @()
    $exit = Invoke-Helper
    Assert-True ($exit -eq 3) "Patcher/config deletion returned unexpected exit code $exit."
    Assert-True (-not (Test-Path -LiteralPath $patchLive)) 'Owned patcher-root file was not retired.'
    Assert-True (-not (Test-Path -LiteralPath $configLive)) 'Owned allowlisted-config-root fixture was not retired.'

    Reset-Sandbox
    $protectedRel = 'BepInEx.cfg'
    $protectedPath = Join-Path (Join-Path $bepInExRoot 'config') $protectedRel
    New-Item -ItemType Directory -Path (Split-Path -Parent $protectedPath) -Force | Out-Null
    Write-Utf8 $protectedPath 'DO-NOT-DELETE'
    $protectedSha = Get-ShaHex 'DO-NOT-DELETE'
    Write-Utf8 $pendingPath ('AMSPENDING2' + $crlf + 'D|C|13|' + $protectedSha + '|' + (B64 $protectedRel) + $crlf)
    Write-OwnershipPending $fpA @()
    $exit = Invoke-Helper
    Assert-True ($exit -eq 1) "Protected-config deletion returned unexpected exit code $exit."
    Assert-True ((Test-Path -LiteralPath $protectedPath) -and ([IO.File]::ReadAllText($protectedPath) -eq 'DO-NOT-DELETE')) 'Protected config was modified/deleted.'
    Write-Host '  PASS'

    Write-Host ''
    Write-Host 'PASS: all Phase 6 ownership/apply checks passed.'
    Write-Host 'No real Valheim installation, trust store, or server files were modified.'

    if (-not $KeepSandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    else { Write-Host "Sandbox retained: $sandbox" }
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('FAIL: ' + $_.Exception.Message)
    Write-Host "Sandbox retained for inspection: $sandbox"
    exit 1
}
