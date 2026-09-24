param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 1 Windows path-boundary validation.
# Compiles the production AutoModSync.PathSafety source into an isolated harness and exercises
# lexical Windows-path rejection, exact-root handling, fixed-root containment, and existing junction rejection.
# No real Valheim/BepInEx installation, trust store, server files, or release artifacts are touched.

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pathSource = Join-Path $repoRoot 'Source\AutoModSync.PathSafety.cs'
if (-not (Test-Path -LiteralPath $pathSource -PathType Leaf)) { throw "Missing source: $pathSource" }

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase1-Paths-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$harnessPath = Join-Path $sandbox 'Phase1PathHarness.cs'
$exePath = Join-Path $sandbox 'Phase1PathHarness.exe'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$harness = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ValheimAutoModSync
{
    internal static class Phase1PathHarness
    {
        private static int _passed;

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _passed++;
        }

        private static void AssertRejected(string value, string label)
        {
            Assert(String.IsNullOrEmpty(AutoModSyncPathSafety.NormalizeRelative(value)), label + " was accepted.");
        }

        private static void AssertThrows(Action action, string label)
        {
            bool threw = false;
            try { action(); }
            catch (InvalidDataException) { threw = true; }
            Assert(threw, label + " did not throw InvalidDataException.");
        }

        private static void CreateJunction(string link, string target)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /c mklink /J " + Quote(link) + " " + Quote(target);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new Exception("Unable to create test junction. exit=" + p.ExitCode + " stdout=" + stdout + " stderr=" + stderr);
            }
        }

        private static string Quote(string value)
        {
            // The test sandbox paths are generated locally and cannot contain a double quote.
            // Build the surrounding quotes explicitly so the PowerShell here-string cannot corrupt C# escaping.
            string q = ((char)34).ToString();
            return q + (value ?? "") + q;
        }

        public static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            string sandbox = Path.GetFullPath(args[0]);
            string root = Path.Combine(sandbox, "trusted-root");
            string outside = Path.Combine(sandbox, "outside");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);

            try
            {
                Console.WriteLine("AutoModSync 2.6 Phase 1 path-boundary validation");

                Console.WriteLine("[1] Canonical safe relative path is accepted...");
                Assert(String.Equals(AutoModSyncPathSafety.NormalizeRelative(@"Nested\file.dll"), "Nested/file.dll", StringComparison.Ordinal), "Safe path did not canonicalize.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2] Parent traversal is rejected...");
                AssertRejected(@"..\evil.dll", "Parent traversal");
                AssertRejected(@"safe\..\evil.dll", "Embedded parent traversal");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3] Rooted/drive-style paths are rejected...");
                AssertRejected(@"C:\evil.dll", "Drive-rooted path");
                AssertRejected(@"\evil.dll", "Backslash-rooted path");
                AssertRejected(@"/evil.dll", "Slash-rooted path");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4] Windows reserved device-name segments are rejected...");
                AssertRejected("CON.dll", "CON");
                AssertRejected(@"Nested\NUL.txt", "NUL");
                AssertRejected(@"Nested\COM1.json", "COM1");
                Console.WriteLine("  PASS");

                Console.WriteLine("[5] Trailing-dot/space aliases are rejected...");
                AssertRejected("name.", "Trailing dot");
                AssertRejected("name ", "Trailing space");
                Console.WriteLine("  PASS");

                Console.WriteLine("[6] Control/invalid filename characters are rejected...");
                AssertRejected("bad" + ((char)1).ToString() + "name.dll", "Control character");
                AssertRejected("bad<name>.dll", "Invalid filename character");
                Console.WriteLine("  PASS");

                Console.WriteLine("[7] Excessive segment/path lengths are rejected...");
                AssertRejected(new string('a', 256) + ".dll", "Oversized segment");
                AssertRejected(new string('a', 4097), "Oversized relative path");
                Console.WriteLine("  PASS");

                Console.WriteLine("[8] Exact trusted root is valid for reparse checking...");
                AutoModSyncPathSafety.EnsureNoReparsePoints(root, root, true);
                Console.WriteLine("  PASS");

                Console.WriteLine("[9] Ordinary descendant remains inside the fixed root...");
                string safeDir = Path.Combine(root, "safe");
                Directory.CreateDirectory(safeDir);
                string safeFile = Path.Combine(safeDir, "file.txt");
                File.WriteAllText(safeFile, "SAFE", new UTF8Encoding(false));
                string resolved = AutoModSyncPathSafety.SafeUnderRoot(root, @"safe\file.txt", true);
                Assert(String.Equals(Path.GetFullPath(safeFile), resolved, StringComparison.OrdinalIgnoreCase), "Safe descendant resolved incorrectly.");
                Console.WriteLine("  PASS");

                Console.WriteLine("[10] Sibling-prefix escape is rejected...");
                string sibling = root + "-sibling";
                Directory.CreateDirectory(sibling);
                string siblingFile = Path.Combine(sibling, "file.txt");
                File.WriteAllText(siblingFile, "OUTSIDE", new UTF8Encoding(false));
                AssertThrows(delegate { AutoModSyncPathSafety.EnsureNoReparsePoints(root, siblingFile, true); }, "Sibling-prefix escape");
                Console.WriteLine("  PASS");

                Console.WriteLine("[11] Existing junction beneath the trusted root is rejected...");
                string outsideFile = Path.Combine(outside, "outside.txt");
                File.WriteAllText(outsideFile, "OUTSIDE", new UTF8Encoding(false));
                string junction = Path.Combine(root, "junction");
                CreateJunction(junction, outside);
                Assert(AutoModSyncPathSafety.IsReparsePoint(junction), "Created junction was not recognized as a reparse point.");
                AssertThrows(delegate { AutoModSyncPathSafety.EnsureNoReparsePoints(root, Path.Combine(junction, "outside.txt"), true); }, "Junction traversal");
                AssertThrows(delegate { AutoModSyncPathSafety.SafeUnderRoot(root, @"junction\outside.txt", true); }, "SafeUnderRoot junction traversal");
                Console.WriteLine("  PASS");

                Console.WriteLine();
                Console.WriteLine("PASS: Phase 1 path-boundary validation passed (" + _passed + " assertions).");
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
    Write-Host "FAIL: Phase 1 path harness compilation failed. Sandbox retained: $sandbox"
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
