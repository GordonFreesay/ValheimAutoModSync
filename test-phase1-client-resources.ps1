param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 1 client resource-limit validation.
# Compiles the production client resource-safety policy into an isolated harness and verifies the production
# client routes manifest/header/chunk/extraction decisions through that policy. No large files are allocated:
# boundary failures are exercised with arithmetic and small in-memory streams.

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repoRoot 'Source'
$policySource = Join-Path $source 'AutoModSync.ClientResourceSafety.cs'
$clientSource = Join-Path $source 'ValheimAutoModSync.Client.cs'
foreach ($p in @($policySource,$clientSource)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing source: $p" }
}

$clientText = [IO.File]::ReadAllText($clientSource)
$requiredWiring = @(
    'AutoModSyncClientResourceSafety.AddRequiredFile(',
    'AutoModSyncClientResourceSafety.ValidateRequiredFileCount(',
    'AutoModSyncClientResourceSafety.ValidateBundleHeader(',
    'AutoModSyncClientResourceSafety.ValidateIncomingChunk(',
    'AutoModSyncClientResourceSafety.CopyZipEntryBounded('
)
foreach ($needle in $requiredWiring) {
    if ($clientText.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
        throw "Production client resource-safety wiring is missing: $needle"
    }
}

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase1-ClientResources-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$harnessPath = Join-Path $sandbox 'Phase1ClientResourceHarness.cs'
$exePath = Join-Path $sandbox 'Phase1ClientResourceHarness.exe'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$harness = @'
using System;
using System.IO;

namespace ValheimAutoModSync
{
    internal static class Phase1ClientResourceHarness
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

        private static string GoodSha()
        {
            return new string('a', 64);
        }

        public static int Main(string[] args)
        {
            try
            {
                Console.WriteLine("AutoModSync 2.6 Phase 1 client resource-limit validation");

                Console.WriteLine("[1/10] Bundle declaration over 2048 MiB is rejected before any archive write...");
                AssertInvalid(delegate
                {
                    int normalized;
                    AutoModSyncClientResourceSafety.ValidateBundleHeader(
                        (AutoModSyncClientResourceSafety.MaxIncomingBundleBytes + 1L).ToString(),
                        GoodSha(), 1, 1, 1, false, 0, 0, out normalized);
                }, "Oversized compressed bundle");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/10] Required-file count above 4096 is rejected...");
                AutoModSyncClientResourceSafety.ValidateRequiredFileCount(AutoModSyncClientResourceSafety.MaxBundleFiles);
                AssertInvalid(delegate
                {
                    AutoModSyncClientResourceSafety.ValidateRequiredFileCount(AutoModSyncClientResourceSafety.MaxBundleFiles + 1);
                }, "Oversized required-file count");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/10] Individual required file above 512 MiB is rejected...");
                AssertInvalid(delegate
                {
                    AutoModSyncClientResourceSafety.AddRequiredFile(
                        AutoModSyncClientResourceSafety.MaxIndividualSyncFileBytes + 1L, 0L, "too-large.dll");
                }, "Oversized individual file");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4/10] Expanded required content above 4096 MiB is rejected without allocating it...");
                AssertInvalid(delegate
                {
                    AutoModSyncClientResourceSafety.AddRequiredFile(
                        2L,
                        AutoModSyncClientResourceSafety.MaxExpandedSyncBytes - 1L,
                        "overflow.dll");
                }, "Expanded content overflow");
                Console.WriteLine("  PASS");

                Console.WriteLine("[5/10] Implausible/excessive chunk count is rejected...");
                AssertInvalid(delegate
                {
                    int normalized;
                    AutoModSyncClientResourceSafety.ValidateBundleHeader(
                        "1", GoodSha(), AutoModSyncClientResourceSafety.MaxBundleChunks + 1,
                        1, 1, false, 0, 0, out normalized);
                }, "Oversized chunk count");
                Console.WriteLine("  PASS");

                Console.WriteLine("[6/10] Non-hex and non-64-character SHA-256 text is rejected...");
                Assert(!AutoModSyncClientResourceSafety.IsSha256Hex(new string('a', 63)), "63-character SHA text was accepted.");
                Assert(!AutoModSyncClientResourceSafety.IsSha256Hex(new string('z', 64)), "Non-hex SHA text was accepted.");
                Assert(AutoModSyncClientResourceSafety.IsSha256Hex(GoodSha()), "Valid SHA-256 text was rejected.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[7/10] Incoming legacy/binary chunk cannot write past declared compressed size...");
                AssertInvalid(delegate
                {
                    AutoModSyncClientResourceSafety.ValidateIncomingChunk(
                        0, 4, 8L, 10L, 1, false, 0);
                }, "Chunk beyond declared bundle size");
                Console.WriteLine("  PASS");

                Console.WriteLine("[8/10] Resume-capable chunk geometry must exactly match the immutable artifact...");
                AssertInvalid(delegate
                {
                    AutoModSyncClientResourceSafety.ValidateIncomingChunk(
                        0, 4095, 0L, 8192L, 2, true, 4096);
                }, "Wrong resumable chunk length");
                AutoModSyncClientResourceSafety.ValidateIncomingChunk(
                    0, 4096, 0L, 8192L, 2, true, 4096);
                Console.WriteLine("  PASS");

                Console.WriteLine("[9/10] ZIP stream expanding beyond its signed entry size is stopped before excess bytes are written...");
                byte[] tooMuch = new byte[] { 1, 2, 3, 4 };
                long cumulative = 0L;
                MemoryStream output = new MemoryStream();
                AssertInvalid(delegate
                {
                    using (MemoryStream input = new MemoryStream(tooMuch))
                        AutoModSyncClientResourceSafety.CopyZipEntryBounded(input, output, 3L, ref cumulative);
                }, "ZIP entry beyond signed size");
                Assert(output.Length <= 3L, "Excess ZIP bytes were written before rejection.");
                Assert(cumulative <= 3L, "Cumulative expanded counter exceeded the signed entry allowance.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[10/10] Cumulative ZIP expansion cannot cross the 4096 MiB ceiling...");
                cumulative = AutoModSyncClientResourceSafety.MaxExpandedSyncBytes - 2L;
                output = new MemoryStream();
                AssertInvalid(delegate
                {
                    using (MemoryStream input = new MemoryStream(new byte[] { 1, 2, 3 }))
                        AutoModSyncClientResourceSafety.CopyZipEntryBounded(input, output, 3L, ref cumulative);
                }, "Cumulative ZIP expansion overflow");
                Assert(output.Length == 0L, "Bytes were written after cumulative expansion should have been rejected.");
                Assert(cumulative == AutoModSyncClientResourceSafety.MaxExpandedSyncBytes - 2L, "Cumulative counter changed on rejected expansion.");
                Console.WriteLine("  PASS");

                Console.WriteLine();
                Console.WriteLine("PASS: Phase 1 client resource-limit validation passed (" + _assertions + " assertions).");
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
    Write-Host "FAIL: Phase 1 client resource harness compilation failed. Sandbox retained: $sandbox"
    exit $LASTEXITCODE
}

& $exePath
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0 -and -not $KeepSandbox) {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
else {
    Write-Host "Sandbox retained: $sandbox"
}

exit $exitCode
