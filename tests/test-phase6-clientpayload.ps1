param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 6 client-payload / prior-server policy validation.
# Compiles the production ClientPayload, OwnershipState, and PathSafety sources into an isolated harness.
# No Valheim installation, BepInEx profile, trust store, or server file is touched.

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'Source'
$payloadSource = Join-Path $source 'AutoModSync.ClientPayload.cs'
$ownershipSource = Join-Path $source 'AutoModSync.OwnershipState.cs'
$pathSource = Join-Path $source 'AutoModSync.PathSafety.cs'

foreach ($path in @($payloadSource,$ownershipSource,$pathSource)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing source: $path" }
}

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase6-ClientPayload-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$harnessPath = Join-Path $sandbox 'ClientPayloadHarness.cs'
$exePath = Join-Path $sandbox 'ClientPayloadHarness.exe'

$harness = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ValheimAutoModSync
{
    internal static class ClientPayloadHarness
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private static string ShaText(string text)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static string ShaFile(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        public static int Main(string[] args)
        {
            string sandbox = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "AMS26-ClientPayloadHarness");
            string payloadRoot = Path.Combine(sandbox, "BepInEx", "AutoModSync", "ClientPayload", "plugins");
            string amsRoot = Path.Combine(sandbox, "ClientState", "BepInEx", "AutoModSync");
            Directory.CreateDirectory(Path.Combine(payloadRoot, "Nested"));
            Directory.CreateDirectory(amsRoot);

            string rootFile = Path.Combine(payloadRoot, "client-only.dll");
            string nestedFile = Path.Combine(payloadRoot, "Nested", "asset.bin");
            string skippedFile = Path.Combine(payloadRoot, "skip.txt");
            File.WriteAllText(rootFile, "CLIENT-ONLY-PLUGIN");
            File.WriteAllText(nestedFile, "NESTED-ASSET");
            File.WriteAllText(skippedFile, "SKIP-ME");

            string fpA = ShaText("phase6-prior-server-A");
            string fpB = ShaText("phase6-prior-server-B");

            try
            {
                Console.WriteLine("[1/5] Fixed client-only plugin root is scanned recursively with exact hashes...");
                List<AutoModSyncClientPayloadFile> all = AutoModSyncClientPayload.CollectPluginFiles(payloadRoot, null);
                Assert(all.Count == 3, "recursive payload scan did not return all files");
                Dictionary<string, AutoModSyncClientPayloadFile> byRel = new Dictionary<string, AutoModSyncClientPayloadFile>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < all.Count; i++) byRel[all[i].RelativePath] = all[i];
                Assert(byRel.ContainsKey("client-only.dll"), "root payload file missing");
                Assert(byRel.ContainsKey("Nested/asset.bin"), "nested payload file missing");
                Assert(byRel["Nested/asset.bin"].Sha256 == ShaFile(nestedFile), "nested payload hash mismatch");
                Assert(byRel["client-only.dll"].Size == new FileInfo(rootFile).Length, "payload size mismatch");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/5] Existing exclusion policy can remove a client-only payload file...");
                List<AutoModSyncClientPayloadFile> filtered = AutoModSyncClientPayload.CollectPluginFiles(
                    payloadRoot,
                    delegate(string relative, string name) { return String.Equals(name, "skip.txt", StringComparison.OrdinalIgnoreCase); });
                Assert(filtered.Count == 2, "exclusion predicate did not remove exactly one payload file");
                for (int i = 0; i < filtered.Count; i++)
                    Assert(!String.Equals(filtered[i].RelativePath, "skip.txt", StringComparison.OrdinalIgnoreCase), "excluded payload survived");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/5] Case-insensitive destination collision fails explicitly while different kinds may share a relative name...");
                Dictionary<string, string> destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AutoModSyncClientPayload.RegisterUniqueDestination(destinations, 'P', "Nested/asset.bin", "normal plugins");
                bool collisionThrown = false;
                try
                {
                    AutoModSyncClientPayload.RegisterUniqueDestination(destinations, 'P', "nested/ASSET.bin", "ClientPayload/plugins");
                }
                catch (InvalidDataException) { collisionThrown = true; }
                Assert(collisionThrown, "case-insensitive P destination collision was silently accepted");
                AutoModSyncClientPayload.RegisterUniqueDestination(destinations, 'R', "Nested/asset.bin", "patchers");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4/5] Stale-deletion authority requires the exact immediately prior successful fingerprint...");
                Assert(!AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpA), "missing prior-server marker granted authority");
                AutoModSyncOwnershipState.WriteLastSuccessfulServerDurable(amsRoot, fpA);
                Assert(AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpA), "exact prior server was not recognized");
                Assert(!AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpB), "different server inherited prior-server authority");
                Console.WriteLine("  PASS");

                Console.WriteLine("[5/5] Malformed prior-server state disables deletion authority and a later successful server safely replaces it...");
                File.WriteAllText(Path.Combine(amsRoot, AutoModSyncOwnershipState.LastSuccessfulServerFileName), "NOT-A-FINGERPRINT");
                Assert(!AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpA), "malformed marker granted server-A authority");
                Assert(!AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpB), "malformed marker granted server-B authority");
                AutoModSyncOwnershipState.WriteLastSuccessfulServerDurable(amsRoot, fpB);
                Assert(!AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpA), "server-A remained prior after server-B publication");
                Assert(AutoModSyncOwnershipState.WasLastSuccessfulServer(amsRoot, fpB), "server-B was not recorded as immediately prior");
                Console.WriteLine("  PASS");

                Console.WriteLine();
                Console.WriteLine("PASS: all Phase 6 client-payload/prior-server checks passed.");
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

[System.IO.File]::WriteAllText($harnessPath, $harness, (New-Object System.Text.UTF8Encoding($false)))

& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$exePath $harnessPath $payloadSource $ownershipSource $pathSource
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 6 client-payload harness compilation failed. Sandbox retained: $sandbox"
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
