param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 1 server resource-limit validation.
# Compiles the production server resource-safety helper and verifies the production server routes
# request accumulation, ZIP-length checks, and failed-build temporary cleanup through it.
# No real Valheim/BepInEx installation is touched and no large files are allocated.

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repoRoot 'Source'
$policySource = Join-Path $source 'AutoModSync.ServerResourceSafety.cs'
$serverSource = Join-Path $source 'ValheimAutoModSync.Server.cs'
foreach ($p in @($policySource,$serverSource)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing source: $p" }
}

$serverText = [IO.File]::ReadAllText($serverSource)
$requiredWiring = @(
    'expandedBytes = AutoModSyncServerResourceSafety.AddExpandedSource(',
    'AutoModSyncServerResourceSafety.EnsureCompressedWithinLimit(output.Length, maxBundleBytes);',
    'AutoModSyncServerResourceSafety.EnsureCompressedWithinLimit(compressedBytes, maxBundleBytes);',
    'AutoModSyncServerResourceSafety.DeleteUnpublishedTemp(tempPath);'
)
foreach ($needle in $requiredWiring) {
    if ($serverText.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Production server resource-safety wiring is missing: $needle"
    }
}

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase1-ServerResources-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$harnessPath = Join-Path $sandbox 'Phase1ServerResourceHarness.cs'
$exePath = Join-Path $sandbox 'Phase1ServerResourceHarness.exe'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$harness = @'
using System;
using System.IO;

namespace ValheimAutoModSync
{
    internal static class Phase1ServerResourceHarness
    {
        private static int _assertions;

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _assertions++;
        }

        private static void AssertInvalid(Action action, string label)
        {
            bool threw = false;
            try { action(); }
            catch (InvalidDataException) { threw = true; }
            Assert(threw, label + " was accepted.");
        }

        public static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            string sandbox = Path.GetFullPath(args[0]);

            try
            {
                Console.WriteLine("AutoModSync 2.6 Phase 1 server resource-limit validation");

                Console.WriteLine("[1/4] Expanded request exactly at the configured ceiling is accepted...");
                long maxExpanded = 4096L * 1024L * 1024L;
                long total = AutoModSyncServerResourceSafety.AddExpandedSource(
                    1L, maxExpanded - 1L, maxExpanded, "expanded limit");
                Assert(total == maxExpanded, "Exact expanded ceiling produced the wrong total.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/4] Expanded request above MaxExpandedBundleMiB is rejected before ZIP construction...");
                AssertInvalid(delegate
                {
                    AutoModSyncServerResourceSafety.AddExpandedSource(
                        2L, maxExpanded - 1L, maxExpanded, "expanded limit");
                }, "Expanded source overflow");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/4] Compressed ZIP length is rejected immediately after it crosses MaxBundleMiB...");
                long maxBundle = 2048L * 1024L * 1024L;
                AutoModSyncServerResourceSafety.EnsureCompressedWithinLimit(maxBundle, maxBundle);
                AssertInvalid(delegate
                {
                    AutoModSyncServerResourceSafety.EnsureCompressedWithinLimit(maxBundle + 1L, maxBundle);
                }, "Compressed ZIP overflow");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4/4] Failed-build cleanup removes an unpublished private bundle temp file...");
                string temp = Path.Combine(sandbox, "bundle-build-test.tmp");
                File.WriteAllText(temp, "PRIVATE-INCOMPLETE");
                Assert(File.Exists(temp), "Failed-build fixture was not created.");
                AutoModSyncServerResourceSafety.DeleteUnpublishedTemp(temp);
                Assert(!File.Exists(temp), "Unpublished failed-build temp file remained.");
                Console.WriteLine("  PASS");

                Console.WriteLine();
                Console.WriteLine("PASS: Phase 1 server resource-limit validation passed (" + _assertions + " assertions).");
                Console.WriteLine("No large files were allocated and no real Valheim/BepInEx installation was modified.");
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

& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$exePath $policySource $harnessPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 1 server resource harness compilation failed. Sandbox retained: $sandbox"
    exit $LASTEXITCODE
}

& $exePath $sandbox
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0 -and -not $KeepSandbox) {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
else {
    Write-Host "Sandbox retained: $sandbox"
}

exit $exitCode
