param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'Source\AutoModSync.IdentityDisplay.cs'
$client = Join-Path $root 'Source\ValheimAutoModSync.Client.cs'
$server = Join-Path $root 'Source\ValheimAutoModSync.Server.cs'
$buildTool = Join-Path $root 'Source\AutoModSync.BuildTool.cs'
$temp = Join-Path $env:TEMP ('AMS26-Phase7-Identity-' + $PID)
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Assert-Equal([string]$Actual,[string]$Expected,[string]$Label) {
    if ($Actual -ne $Expected) { throw ($Label + ': expected [' + $Expected + '] but got [' + $Actual + '].') }
}

try {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw '.NET Framework C# compiler was not found.' }

    $harness = Join-Path $temp 'IdentityHarness.cs'
    @'
using System;

namespace ValheimAutoModSync
{
    internal static class IdentityHarness
    {
        private static int Main()
        {
            string fp = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            if (AutoModSyncIdentityDisplay.VerificationCode(fp) != "0123-4567-89AB-CDEF") return 10;
            if (AutoModSyncIdentityDisplay.VerificationCode("bad") != "UNAVAILABLE") return 11;
            if (AutoModSyncIdentityDisplay.FullFingerprint(fp) !=
                "01234567-89ABCDEF-01234567-89ABCDEF-01234567-89ABCDEF-01234567-89ABCDEF") return 12;
            if (!AutoModSyncIdentityDisplay.IsValidFingerprint(fp)) return 13;
            if (AutoModSyncIdentityDisplay.IsValidFingerprint(fp.Substring(0, 63) + "g")) return 14;
            return 0;
        }
    }
}
'@ | Set-Content -LiteralPath $harness -Encoding UTF8

    $exe = Join-Path $temp 'IdentityHarness.exe'
    & $csc /nologo /target:exe /optimize+ /langversion:5 /out:$exe $source $harness
    if ($LASTEXITCODE -ne 0) { throw "Identity helper harness compilation failed with exit code $LASTEXITCODE." }

    & $exe
    if ($LASTEXITCODE -ne 0) { throw "Identity helper harness failed with exit code $LASTEXITCODE." }
    Write-Host '[1/4] PASS 64-bit comparison code and full technical formatting are deterministic.'

    $clientText = [IO.File]::ReadAllText($client)
    foreach ($required in @(
        '"SECURITY CODE"',
        'AutoModSyncIdentityDisplay.VerificationCode(_uiState.ServerFingerprint)',
        'TRUSTED SERVER',
        'FIRST CONTACT'
    )) {
        if ($clientText.IndexOf($required,[StringComparison]::Ordinal) -lt 0) {
            throw ('Client privacy UI is missing required wiring: ' + $required)
        }
    }
    Write-Host '[2/4] PASS normal player UI uses the shortened comparison code and trust labels.'

    foreach ($forbidden in @(
        '"SERVER FINGERPRINT"',
        'ShortFingerprint(_uiState.ServerFingerprint)',
        'FormatFingerprint(_uiState.ServerFingerprint)',
        '"AutoModSync server fingerprint: "',
        '"AutoModSync trusted server fingerprint "'
    )) {
        if ($clientText.IndexOf($forbidden,[StringComparison]::Ordinal) -ge 0) {
            throw ('Client privacy UI still contains legacy fingerprint exposure: ' + $forbidden)
        }
    }
    Write-Host '[3/4] PASS legacy full/partial fingerprint presentation is absent from normal client UI.'

    $serverText = [IO.File]::ReadAllText($server)
    if ($serverText.IndexOf('"AutoModSync server fingerprint: "',[StringComparison]::Ordinal) -ge 0) {
        throw 'Normal server log still exposes the complete fingerprint.'
    }
    if ($serverText.IndexOf('AutoModSyncIdentityDisplay.VerificationCode(_publicFingerprint)',[StringComparison]::Ordinal) -lt 0) {
        throw 'Normal server log is missing the shortened identity verification code.'
    }

    $buildToolText = [IO.File]::ReadAllText($buildTool)
    if ($buildToolText.IndexOf('Full server public fingerprint (technical):',[StringComparison]::Ordinal) -lt 0) {
        throw 'Explicit administrator identity tooling no longer exposes the full fingerprint needed for advanced verification.'
    }
    Write-Host '[4/4] PASS normal server logs are privacy-minimized while explicit admin tooling retains full technical identity output.'

    Write-Host 'PASS: Phase 7 server-identity display is privacy-minimized without weakening full-fingerprint pinning.'
}
finally {
    try { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } } catch {}
}
