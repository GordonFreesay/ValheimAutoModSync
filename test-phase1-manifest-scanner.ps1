param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 1 production manifest-scanner validation.
# Compiles the production scanner + path-safety sources into an isolated harness.
# A real Windows junction is always exercised. A file symlink is exercised when the host permits creating one.

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repoRoot 'Source'
$scannerSource = Join-Path $source 'AutoModSync.ManifestScanner.cs'
$pathSource = Join-Path $source 'AutoModSync.PathSafety.cs'
foreach ($p in @($scannerSource,$pathSource)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing source: $p" }
}

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase1-ManifestScanner-' + $PID)
$root = Join-Path $sandbox 'plugins'
$nested = Join-Path $root 'nested'
$outside = Join-Path $sandbox 'outside'
New-Item -ItemType Directory -Path $nested,$outside -Force | Out-Null

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $root 'root.txt'), 'ROOT', $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $nested 'nested.txt'), 'NESTED', $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $outside 'escape.txt'), 'ESCAPE', $utf8NoBom)

$junction = Join-Path $root 'junction'
New-Item -ItemType Junction -Path $junction -Target $outside -Force | Out-Null

$fileSymlink = Join-Path $root 'linked-file.txt'
$fileSymlinkCreated = $false
try {
    New-Item -ItemType SymbolicLink -Path $fileSymlink -Target (Join-Path $outside 'escape.txt') -Force -ErrorAction Stop | Out-Null
    $fileSymlinkCreated = $true
}
catch {
    Write-Host "NOTE: file symlink creation is not permitted on this host; file-reparse scanner case will be reported as SKIP."
}

$harnessPath = Join-Path $sandbox 'Phase1ManifestScannerHarness.cs'
$exePath = Join-Path $sandbox 'Phase1ManifestScannerHarness.exe'

$harness = @'
using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimAutoModSync
{
    internal static class Phase1ManifestScannerHarness
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static bool ContainsPath(List<string> files, string path)
        {
            string full = Path.GetFullPath(path);
            for (int i = 0; i < files.Count; i++)
                if (String.Equals(Path.GetFullPath(files[i]), full, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ContainsUnder(List<string> files, string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            for (int i = 0; i < files.Count; i++)
            {
                string full = Path.GetFullPath(files[i]);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static int Main(string[] args)
        {
            if (args.Length != 3) return 2;
            string root = Path.GetFullPath(args[0]);
            string outside = Path.GetFullPath(args[1]);
            bool fileSymlinkCreated = String.Equals(args[2], "true", StringComparison.OrdinalIgnoreCase);

            try
            {
                List<string> warnings = new List<string>();
                List<string> files = AutoModSyncManifestScanner.EnumerateFiles(
                    root,
                    delegate(string message) { warnings.Add(message ?? ""); });

                Console.WriteLine("AutoModSync 2.6 Phase 1 manifest-scanner validation");

                Console.WriteLine("[1] Ordinary root/nested files are enumerated...");
                Assert(ContainsPath(files, Path.Combine(root, "root.txt")), "Root file was not enumerated.");
                Assert(ContainsPath(files, Path.Combine(root, "nested", "nested.txt")), "Nested file was not enumerated.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2] Directory junction is identified as a reparse point...");
                string junction = Path.Combine(root, "junction");
                Assert(AutoModSyncPathSafety.IsReparsePoint(junction), "Test junction is not reported as a reparse point.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3] Scanner does not traverse/publish files through the junction...");
                Assert(!ContainsUnder(files, junction), "Scanner published a path beneath the junction.");
                Assert(!ContainsPath(files, Path.Combine(junction, "escape.txt")), "Scanner published the junction target file.");
                bool sawDirectoryWarning = false;
                for (int i = 0; i < warnings.Count; i++)
                    if (warnings[i].IndexOf("reparse-point source directory", StringComparison.OrdinalIgnoreCase) >= 0) sawDirectoryWarning = true;
                Assert(sawDirectoryWarning, "Scanner did not report the skipped reparse-point directory.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4] Outside target is never published as though it belonged to the fixed root...");
                Assert(!ContainsPath(files, Path.Combine(outside, "escape.txt")), "Scanner published the outside target directly.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[5] Reparse-point source file is excluded when this Windows host permits a file symlink...");
                if (!fileSymlinkCreated)
                {
                    Console.WriteLine("  SKIP: host denied file-symlink creation.");
                }
                else
                {
                    string linkedFile = Path.Combine(root, "linked-file.txt");
                    Assert(AutoModSyncPathSafety.IsReparsePoint(linkedFile), "Test file symlink is not reported as a reparse point.");
                    Assert(!ContainsPath(files, linkedFile), "Scanner published a reparse-point source file.");
                    bool sawFileWarning = false;
                    for (int i = 0; i < warnings.Count; i++)
                        if (warnings[i].IndexOf("reparse-point source file", StringComparison.OrdinalIgnoreCase) >= 0) sawFileWarning = true;
                    Assert(sawFileWarning, "Scanner did not report the skipped reparse-point source file.");
                    Console.WriteLine("  PASS");
                }

                Console.WriteLine();
                Console.WriteLine("PASS: production manifest scanner stayed inside its fixed source tree.");
                Console.WriteLine("File-reparse case: " + (fileSymlinkCreated ? "PASS" : "SKIP"));
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

& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$exePath $pathSource $scannerSource $harnessPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 1 manifest-scanner harness compilation failed. Sandbox retained: $sandbox"
    exit $LASTEXITCODE
}

& $exePath $root $outside ($fileSymlinkCreated.ToString().ToLowerInvariant())
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0 -and -not $KeepSandbox) {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
else {
    Write-Host "Sandbox retained: $sandbox"
}

exit $exitCode
