param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('PrepareClient','SeedOwnership','InspectClientUninstall','PrepareServer','SeedServerPreservation','InspectServerUninstall','Cleanup')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 isolated standalone-installer live gate.
# Intent: creates a disposable fake Valheim root so the GUI installer/uninstaller can be exercised without mutating the maintainer's real game installation.

$root = Join-Path $env:TEMP 'AMS26-Installer-Live'
$bep = Join-Path $root 'BepInEx'
$ams = Join-Path $bep 'AutoModSync'
$pluginRoot = Join-Path $bep 'plugins'
$fixtureRoot = Join-Path $pluginRoot '__AMS_INSTALLER_TEST__'
$owned = Join-Path $fixtureRoot 'owned.txt'
$modified = Join-Path $fixtureRoot 'modified.txt'
$unrelated = Join-Path $pluginRoot 'UnrelatedLocalPlugin.dll'
$ledgerDir = Join-Path $ams 'ownership'
$fingerprint = ('a' * 64)

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

switch ($Action) {
    'PrepareClient' {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $root 'valheim.exe'),[byte[]]@(0))
        Write-Host 'Prepared disposable installer test root:'
        Write-Host ('  ' + $root)
        Write-Host ''
        Write-Host 'Open the freshly built ValheimAutoModSyncInstaller.exe, choose Client, Browse to this folder, and click Install.'
        Write-Host 'After INSTALL COMPLETE, close the installer and run this script with -Action SeedOwnership.'
    }

    'SeedOwnership' {
        $clientDll = Join-Path $pluginRoot 'ValheimAutoModSync.Client.dll'
        $applyExe = Join-Path $ams 'ValheimAutoModSync.Apply.exe'
        $bepDll = Join-Path $bep 'core\BepInEx.dll'
        foreach ($required in @($clientDll,$applyExe,$bepDll)) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
                throw ('Client install is incomplete; missing ' + $required)
            }
        }

        New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $ledgerDir -Force | Out-Null
        [IO.File]::WriteAllText($owned,'owned-by-ams',[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($modified,'original-owned-by-ams',[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($unrelated,'unrelated-local-plugin',[Text.UTF8Encoding]::new($false))

        $ownedSize = (Get-Item -LiteralPath $owned).Length
        $ownedSha = Get-Sha256 $owned
        $modifiedOriginalSize = (Get-Item -LiteralPath $modified).Length
        $modifiedOriginalSha = Get-Sha256 $modified

        $ownedRel = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('__AMS_INSTALLER_TEST__/owned.txt'))
        $modifiedRel = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('__AMS_INSTALLER_TEST__/modified.txt'))
        $ledger = @(
            'AMSOWN1',
            $fingerprint,
            ('P|' + $ownedSize + '|' + $ownedSha + '|' + $ownedRel),
            ('P|' + $modifiedOriginalSize + '|' + $modifiedOriginalSha + '|' + $modifiedRel)
        )
        [IO.File]::WriteAllLines((Join-Path $ledgerDir ($fingerprint + '.txt')),$ledger,[Text.UTF8Encoding]::new($false))

        # Deliberately modify one AMS-owned file after the ownership record is written.
        [IO.File]::WriteAllText($modified,'locally-modified-after-sync',[Text.UTF8Encoding]::new($false))

        Write-Host 'Seeded ownership-safe uninstall fixtures.'
        Write-Host 'Reopen the installer, choose Client, Browse to the same temp folder.'
        Write-Host 'Expected: status says Installed, button says Repair / Update, and Uninstall is visible.'
        Write-Host 'Click Uninstall and confirm. Then run -Action InspectClientUninstall.'
    }

    'InspectClientUninstall' {
        $fail = $false
        if (Test-Path -LiteralPath (Join-Path $pluginRoot 'ValheimAutoModSync.Client.dll')) { Write-Host 'FAIL client plugin still exists.'; $fail = $true } else { Write-Host 'PASS client plugin removed.' }
        if (Test-Path -LiteralPath (Join-Path $ams 'ValheimAutoModSync.Apply.exe')) { Write-Host 'FAIL apply helper still exists.'; $fail = $true } else { Write-Host 'PASS apply helper removed.' }
        if (Test-Path -LiteralPath $owned) { Write-Host 'FAIL exact AMS-owned fixture still exists.'; $fail = $true } else { Write-Host 'PASS exact AMS-owned fixture retired.' }
        if (-not (Test-Path -LiteralPath $modified)) { Write-Host 'FAIL locally modified fixture was deleted.'; $fail = $true } else { Write-Host 'PASS locally modified fixture preserved.' }
        if (-not (Test-Path -LiteralPath $unrelated)) { Write-Host 'FAIL unrelated local plugin was deleted.'; $fail = $true } else { Write-Host 'PASS unrelated local plugin preserved.' }
        if (-not (Test-Path -LiteralPath (Join-Path $bep 'core\BepInEx.dll'))) { Write-Host 'FAIL shared BepInEx was removed.'; $fail = $true } else { Write-Host 'PASS shared BepInEx preserved.' }

        if ($fail) { exit 1 }
        Write-Host ''
        Write-Host 'PASS: isolated installer client uninstall is ownership-safe and preserves shared/local content.'
    }

    'PrepareServer' {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $root 'valheim_server.exe'),[byte[]]@(0))
        Write-Host 'Prepared disposable dedicated-server installer test root:'
        Write-Host ('  ' + $root)
        Write-Host ''
        Write-Host 'Open the freshly built ValheimAutoModSyncInstaller.exe, choose Dedicated Server, Browse to this folder, and click Install.'
        Write-Host 'After INSTALL COMPLETE, close the installer and run this script with -Action SeedServerPreservation.'
    }

    'SeedServerPreservation' {
        $serverDll = Join-Path $pluginRoot 'ValheimAutoModSync.Server.dll'
        $releaseClient = Join-Path $ams 'release\ValheimAutoModSync.Client.dll'
        $serverConfig = Join-Path $bep 'config\com.gordonfreesay.valheimautomodsync.server.cfg'
        $privateKey = Join-Path $bep 'config\ValheimAutoModSync.private.xml'
        $publicKey = Join-Path $bep 'config\ValheimAutoModSync.public.xml'
        $bepDll = Join-Path $bep 'core\BepInEx.dll'
        foreach ($required in @($serverDll,$releaseClient,$serverConfig,$privateKey,$publicKey,$bepDll)) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
                throw ('Server install is incomplete; missing ' + $required)
            }
        }

        $clientPayload = Join-Path $ams 'ClientPayload\plugins'
        New-Item -ItemType Directory -Path $clientPayload -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $clientPayload 'operator-owned.txt'),'preserve-me',[Text.UTF8Encoding]::new($false))

        $snapshot = @(
            ('config=' + (Get-Sha256 $serverConfig)),
            ('private=' + (Get-Sha256 $privateKey)),
            ('public=' + (Get-Sha256 $publicKey))
        )
        [IO.File]::WriteAllLines((Join-Path $root 'server-preserve.sha256'),$snapshot,[Text.UTF8Encoding]::new($false))

        Write-Host 'Seeded server preservation fixtures.'
        Write-Host 'Reopen the installer, choose Dedicated Server, Browse to the same temp folder.'
        Write-Host 'Expected: selected role is complete and Uninstall is visible.'
        Write-Host 'LEAVE "Also remove server config + signing identity" UNCHECKED.'
        Write-Host 'Click Uninstall and confirm. Then run -Action InspectServerUninstall.'
    }

    'InspectServerUninstall' {
        $serverDll = Join-Path $pluginRoot 'ValheimAutoModSync.Server.dll'
        $releaseClient = Join-Path $ams 'release\ValheimAutoModSync.Client.dll'
        $serverConfig = Join-Path $bep 'config\com.gordonfreesay.valheimautomodsync.server.cfg'
        $privateKey = Join-Path $bep 'config\ValheimAutoModSync.private.xml'
        $publicKey = Join-Path $bep 'config\ValheimAutoModSync.public.xml'
        $payload = Join-Path $ams 'ClientPayload\plugins\operator-owned.txt'
        $snapshotPath = Join-Path $root 'server-preserve.sha256'
        if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) { throw 'Server preservation snapshot is missing.' }

        $expected = @{}
        foreach ($line in [IO.File]::ReadAllLines($snapshotPath)) {
            $parts = $line.Split('=')
            if ($parts.Length -eq 2) { $expected[$parts[0]] = $parts[1] }
        }

        $fail = $false
        if (Test-Path -LiteralPath $serverDll) { Write-Host 'FAIL server plugin still exists.'; $fail = $true } else { Write-Host 'PASS server plugin removed.' }
        if (Test-Path -LiteralPath $releaseClient) { Write-Host 'FAIL server release client payload still exists.'; $fail = $true } else { Write-Host 'PASS server release client payload removed.' }
        if (-not (Test-Path -LiteralPath $payload)) { Write-Host 'FAIL operator-managed ClientPayload was removed.'; $fail = $true } else { Write-Host 'PASS operator-managed ClientPayload preserved.' }
        if (-not (Test-Path -LiteralPath (Join-Path $bep 'core\BepInEx.dll'))) { Write-Host 'FAIL shared BepInEx was removed.'; $fail = $true } else { Write-Host 'PASS shared BepInEx preserved.' }

        foreach ($item in @(@('config',$serverConfig),@('private',$privateKey),@('public',$publicKey))) {
            $label = $item[0]
            $file = $item[1]
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                Write-Host ('FAIL preserved server ' + $label + ' file is missing.')
                $fail = $true
            }
            elseif (-not $expected.ContainsKey($label) -or (Get-Sha256 $file) -ne $expected[$label]) {
                Write-Host ('FAIL preserved server ' + $label + ' file changed.')
                $fail = $true
            }
            else {
                Write-Host ('PASS server ' + $label + ' preserved byte-for-byte.')
            }
        }

        if ($fail) { exit 1 }
        Write-Host ''
        Write-Host 'PASS: isolated installer server uninstall removes AMS runtime while preserving shared BepInEx, ClientPayload, config, and signing identity.'
    }

    'Cleanup' {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        Write-Host ('Removed disposable installer test root: ' + $root)
    }
}
