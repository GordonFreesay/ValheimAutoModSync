param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 1 client live-destination path validation.
# Compiles the production AutoModSync.PathSafety source into an isolated harness and also verifies that
# the production client target-path wrapper calls SafeUnderRoot(..., true), so existing leaf components
# are included in the reparse check. No real Valheim/BepInEx installation is touched.

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pathSource = Join-Path $repoRoot 'Source\AutoModSync.PathSafety.cs'
$clientSource = Join-Path $repoRoot 'Source\ValheimAutoModSync.Client.cs'
foreach ($p in @($pathSource,$clientSource)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing source: $p" }
}

$clientText = [IO.File]::ReadAllText($clientSource)
$expectedWiring = 'return AutoModSyncPathSafety.SafeUnderRoot(rootPath, relative, true);'
if ($clientText.IndexOf($expectedWiring, [StringComparison]::Ordinal) -lt 0) {
    throw 'Production client no longer routes BepInEx target paths through SafeUnderRoot(rootPath, relative, true). Update this harness before trusting its result.'
}

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase1-ClientPaths-' + $PID)
$plugins = Join-Path $sandbox 'BepInEx\plugins'
$outside = Join-Path $sandbox 'outside'
New-Item -ItemType Directory -Path $plugins,$outside -Force | Out-Null

$parentTarget = Join-Path $outside 'parent-target'
$leafTarget = Join-Path $outside 'leaf-target'
New-Item -ItemType Directory -Path $parentTarget,$leafTarget -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $parentTarget 'payload.dll'), 'OUTSIDE-PARENT')
[IO.File]::WriteAllText((Join-Path $leafTarget 'marker.txt'), 'OUTSIDE-LEAF')

$parentLink = Join-Path $plugins 'ParentJunction'
New-Item -ItemType Junction -Path $parentLink -Target $parentTarget -Force | Out-Null

$leafLink = Join-Path $plugins 'LeafJunction'
New-Item -ItemType Junction -Path $leafLink -Target $leafTarget -Force | Out-Null

$harnessPath = Join-Path $sandbox 'Phase1ClientPathHarness.cs'
$exePath = Join-Path $sandbox 'Phase1ClientPathHarness.exe'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$harness = @'
using System;
using System.IO;

namespace ValheimAutoModSync
{
    internal static class Phase1ClientPathHarness
    {
        private static void AssertThrows(Action action, string label)
        {
            bool threw = false;
            try { action(); }
            catch (InvalidDataException) { threw = true; }
            if (!threw) throw new Exception(label + " was accepted.");
        }

        public static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            string plugins = Path.GetFullPath(args[0]);

            try
            {
                Console.WriteLine("AutoModSync 2.6 Phase 1 client live-destination validation");

                Console.WriteLine("[1/3] Ordinary destination beneath plugins is accepted...");
                string normal = AutoModSyncPathSafety.SafeUnderRoot(plugins, @"Safe\payload.dll", true);
                string expected = Path.Combine(plugins, "Safe", "payload.dll");
                if (!String.Equals(normal, expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Ordinary destination resolved incorrectly.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/3] Existing parent junction in a client destination is rejected...");
                AssertThrows(
                    delegate { AutoModSyncPathSafety.SafeUnderRoot(plugins, @"ParentJunction\payload.dll", true); },
                    "Parent-junction destination");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/3] Existing leaf reparse point is rejected when client wiring requests includeLeaf=true...");
                AssertThrows(
                    delegate { AutoModSyncPathSafety.SafeUnderRoot(plugins, @"LeafJunction", true); },
                    "Leaf-junction destination");
                Console.WriteLine("  PASS");

                Console.WriteLine();
                Console.WriteLine("PASS: client live-destination path policy rejects existing parent/leaf reparse points.");
                Console.WriteLine("No real Valheim/BepInEx installation or server files were modified.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("FAIL: " + ex);
                return 1;
            }
        }
    }
}
'@

[IO.File]::WriteAllText($harnessPath, $harness, $utf8NoBom)

& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$exePath $pathSource $harnessPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 1 client-path harness compilation failed. Sandbox retained: $sandbox"
    exit $LASTEXITCODE
}

& $exePath $plugins
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0 -and -not $KeepSandbox) {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
else {
    Write-Host "Sandbox retained: $sandbox"
}

exit $exitCode
