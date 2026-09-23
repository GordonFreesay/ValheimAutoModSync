param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 4 deterministic resume validation.
# Compiles the production resume/path-safety sources with a tiny isolated harness and emulates interrupted partial files.
# It does not load Valheim, contact a server, or touch the real BepInEx tree.

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'Source'
$resumeSource = Join-Path $source 'AutoModSync.ResumeState.cs'
$pathSource = Join-Path $source 'AutoModSync.PathSafety.cs'

if (-not (Test-Path -LiteralPath $resumeSource)) { throw "Missing source: $resumeSource" }
if (-not (Test-Path -LiteralPath $pathSource)) { throw "Missing source: $pathSource" }

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase4-Resume-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$harnessPath = Join-Path $sandbox 'ResumeHarness.cs'
$exePath = Join-Path $sandbox 'ResumeHarness.exe'

$harness = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ValheimAutoModSync
{
    internal static class ResumeHarness
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

        private static string ShaFile(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(fs));
        }

        private static string ShaText(string text)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static void CopyPrefix(string source, FileStream destination, long bytes, bool corrupt)
        {
            byte[] buffer = new byte[81920];
            long remaining = bytes;
            bool corrupted = false;
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (remaining > 0)
                {
                    int want = (int)Math.Min((long)buffer.Length, remaining);
                    int read = input.Read(buffer, 0, want);
                    if (read <= 0) throw new EndOfStreamException();
                    if (corrupt && !corrupted)
                    {
                        buffer[0] ^= 0x5A;
                        corrupted = true;
                    }
                    destination.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }

        private static void AppendRemainder(string source, FileStream destination, long offset)
        {
            byte[] buffer = new byte[81920];
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                input.Seek(offset, SeekOrigin.Begin);
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    destination.Write(buffer, 0, read);
                }
                destination.Flush(true);
            }
        }

        public static int Main(string[] args)
        {
            string sandbox = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "AMS26-ResumeHarness");
            string amsRoot = Path.Combine(sandbox, "BepInEx", "AutoModSync");
            Directory.CreateDirectory(amsRoot);
            string artifact = Path.Combine(sandbox, "artifact.bin");

            byte[] data = new byte[(5 * 1024 * 1024) + 12345];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + 17) & 0xFF);
            File.WriteAllBytes(artifact, data);

            string fingerprint = ShaText("phase4-server");
            List<string> rows = new List<string>();
            rows.Add("P\tmods/a.dll\t123\t" + ShaText("a"));
            rows.Add("C\tcfg/test.cfg\t456\t" + ShaText("b"));
            string requestKey = AutoModSyncResumeState.ComputeRequestKey(fingerprint, rows);
            string bundleSha = ShaFile(artifact);
            int chunkBytes = 24576;
            int totalChunks = (int)((data.LongLength + chunkBytes - 1L) / chunkBytes);
            int fileCount = 2;

            try
            {
                Console.WriteLine("[1/4] Interrupted non-boundary write truncates to a complete chunk and resumes exact bytes...");
                string partial;
                using (FileStream output = AutoModSyncResumeState.CreateFreshClientPartial(amsRoot, fingerprint, requestKey, bundleSha, data.LongLength, chunkBytes, totalChunks, fileCount, out partial))
                {
                    CopyPrefix(artifact, output, (long)50 * chunkBytes + 777, false);
                    output.Flush(true);
                }

                AutoModSyncResumeCandidate candidate;
                string reason;
                Assert(AutoModSyncResumeState.TryPrepareClientCandidate(amsRoot, fingerprint, requestKey, 86400, out candidate, out reason), "candidate not prepared: " + reason);
                Assert(candidate.NextChunk == 50, "partial was not rounded down to chunk 50");
                Assert(new FileInfo(partial).Length == (long)50 * chunkBytes, "partial length was not truncated to the complete-chunk boundary");

                long resumeBytes;
                Assert(AutoModSyncResumeState.TryAcceptServerCandidate(artifact, bundleSha, data.LongLength, chunkBytes, totalChunks, fileCount, candidate, out resumeBytes, out reason), "server rejected exact prefix: " + reason);
                FileStream resumed;
                string reopened;
                long reopenedBytes;
                Assert(AutoModSyncResumeState.TryOpenAcceptedClientPartial(amsRoot, fingerprint, requestKey, candidate, out resumed, out reopened, out reopenedBytes, out reason), "client could not reopen accepted prefix: " + reason);
                using (resumed) AppendRemainder(artifact, resumed, reopenedBytes);
                Assert(ShaFile(reopened) == bundleSha, "resumed file did not equal the exact artifact SHA-256");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/4] Corrupt saved prefix is rejected by server-side prefix verification...");
                AutoModSyncResumeState.Discard(amsRoot);
                using (FileStream output = AutoModSyncResumeState.CreateFreshClientPartial(amsRoot, fingerprint, requestKey, bundleSha, data.LongLength, chunkBytes, totalChunks, fileCount, out partial))
                {
                    CopyPrefix(artifact, output, (long)12 * chunkBytes, true);
                    output.Flush(true);
                }
                Assert(AutoModSyncResumeState.TryPrepareClientCandidate(amsRoot, fingerprint, requestKey, 86400, out candidate, out reason), "corrupt prefix was not represented as a candidate");
                Assert(!AutoModSyncResumeState.TryAcceptServerCandidate(artifact, bundleSha, data.LongLength, chunkBytes, totalChunks, fileCount, candidate, out resumeBytes, out reason), "server accepted corrupt prefix");
                Assert(reason.IndexOf("prefix SHA-256", StringComparison.OrdinalIgnoreCase) >= 0, "corrupt prefix rejection reason was unexpected: " + reason);
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/4] Different server/change-set metadata cannot reuse the saved partial...");
                string otherRequest = ShaText("different-change-set");
                Assert(!AutoModSyncResumeState.TryPrepareClientCandidate(amsRoot, fingerprint, otherRequest, 86400, out candidate, out reason), "different request key reused the saved partial");
                Assert(!File.Exists(AutoModSyncResumeState.GetPartialPath(amsRoot)), "mismatched candidate was not discarded");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4/4] Fully downloaded artifact can resume at TotalChunks and still requires exact full SHA...");
                using (FileStream output = AutoModSyncResumeState.CreateFreshClientPartial(amsRoot, fingerprint, requestKey, bundleSha, data.LongLength, chunkBytes, totalChunks, fileCount, out partial))
                {
                    CopyPrefix(artifact, output, data.LongLength, false);
                    output.Flush(true);
                }
                Assert(AutoModSyncResumeState.TryPrepareClientCandidate(amsRoot, fingerprint, requestKey, 86400, out candidate, out reason), "complete candidate not prepared: " + reason);
                Assert(candidate.NextChunk == totalChunks, "complete artifact did not resume at TotalChunks");
                Assert(AutoModSyncResumeState.TryAcceptServerCandidate(artifact, bundleSha, data.LongLength, chunkBytes, totalChunks, fileCount, candidate, out resumeBytes, out reason), "server rejected complete exact artifact: " + reason);
                Assert(resumeBytes == data.LongLength, "complete resume byte count mismatch");
                Assert(ShaFile(partial) == bundleSha, "complete saved artifact failed full SHA-256");
                Console.WriteLine("  PASS");

                AutoModSyncResumeState.Discard(amsRoot);
                Console.WriteLine();
                Console.WriteLine("PASS: all Phase 4 deterministic resume-state checks passed.");
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

& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$exePath $harnessPath $resumeSource $pathSource
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 4 harness compilation failed. Sandbox retained: $sandbox"
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
