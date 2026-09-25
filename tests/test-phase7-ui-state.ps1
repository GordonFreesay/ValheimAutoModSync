param(
    [switch]$KeepSandbox
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 7 deterministic player-facing sync-state validation.
# Compiles only the production UI-state model with a tiny isolated harness.
# It does not load Valheim, open sockets, touch BepInEx, or build release artifacts.

$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$source=Join-Path $root 'Source'
$stateSource=Join-Path $source 'AutoModSync.SyncUiState.cs'
if(-not(Test-Path -LiteralPath $stateSource)){throw "Missing source: $stateSource"}

$csc=$null
if(Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"){$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"}
elseif(Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"){$csc="$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"}
if(-not$csc){throw '.NET Framework C# compiler was not found.'}

$sandbox=Join-Path $env:TEMP ('AMS26-Phase7-UiState-'+$PID)
New-Item -ItemType Directory -Path $sandbox -Force|Out-Null
$harnessPath=Join-Path $sandbox 'UiStateHarness.cs'
$exePath=Join-Path $sandbox 'UiStateHarness.exe'

$harness=@'
using System;

namespace ValheimAutoModSync
{
    internal static class UiStateHarness
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static bool Near(double actual, double expected, double tolerance)
        {
            return Math.Abs(actual - expected) <= tolerance;
        }

        public static int Main(string[] args)
        {
            try
            {
                Console.WriteLine("[1/6] Reset starts hidden with zero stale presentation state...");
                AutoModSyncUiState s = new AutoModSyncUiState();
                Assert(s.Phase == AutoModSyncUiPhase.Hidden, "initial phase was not Hidden");
                Assert(s.ManifestFiles == 0 && s.BundleBytes == 0L && s.VerificationCompleted == 0, "initial counters were not zero");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/6] Signed-manifest comparison counters remain explicit...");
                s.SetPhase(AutoModSyncUiPhase.Comparing, "Comparing required files...", "");
                s.SetComparison(63, 61, 2, 1, 17L * 1024L * 1024L);
                Assert(s.ManifestFiles == 63 && s.MatchedFiles == 61 && s.ChangedFiles == 2 && s.RemovedFiles == 1, "comparison counters changed");
                Assert(s.RequiredExpandedBytes == 17L * 1024L * 1024L, "required expanded bytes changed");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/6] Transfer telemetry computes current/average rate and ETA without controlling pacing...");
                DateTime t0 = new DateTime(638000000000000000L, DateTimeKind.Utc);
                s.BeginTransfer(16L * 1024L * 1024L, 0L, t0);
                s.UpdateTransfer(4L * 1024L * 1024L, t0.AddSeconds(1));
                Assert(Near(s.AverageBytesPerSecond, 4.0 * 1024.0 * 1024.0, 4096.0), "average transfer rate was wrong");
                Assert(Near(s.CurrentBytesPerSecond, 4.0 * 1024.0 * 1024.0, 4096.0), "current transfer rate was wrong");
                Assert(Near(s.TransferEtaSeconds(), 3.0, 0.05), "ETA was wrong");
                Assert(Near(s.TransferProgress(), 0.25, 0.0001), "transfer progress was wrong");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4/6] Resume telemetry excludes retained bytes from session average...");
                s.BeginTransfer(16L * 1024L * 1024L, 8L * 1024L * 1024L, t0);
                s.UpdateTransfer(12L * 1024L * 1024L, t0.AddSeconds(2));
                Assert(s.SessionStartBytes == 8L * 1024L * 1024L, "resume baseline changed");
                Assert(Near(s.AverageBytesPerSecond, 2.0 * 1024.0 * 1024.0, 4096.0), "resume session average counted retained bytes");
                Assert(Near(s.TransferProgress(), 0.75, 0.0001), "resume progress was wrong");
                Console.WriteLine("  PASS");

                Console.WriteLine("[5/6] Verification and queue state are presentation-only bounded counters...");
                s.SetQueue(3, 4, 4);
                Assert(s.QueuePosition == 3 && s.QueueActive == 4 && s.QueueMaxActive == 4, "queue state changed");
                s.BeginVerification(2);
                s.MarkVerified();
                s.MarkVerified();
                s.MarkVerified();
                Assert(s.VerificationCompleted == 2 && s.VerificationTotal == 2, "verification progress exceeded total");
                Console.WriteLine("  PASS");

                Console.WriteLine("[6/6] Reset clears server identity, metrics, and cross-session counters...");
                s.SetServerFingerprint("abcdef");
                s.SetPhase(AutoModSyncUiPhase.Failed, "failed", "detail");
                s.Reset();
                Assert(s.Phase == AutoModSyncUiPhase.Hidden && s.ServerFingerprint == "" && s.Status == "" && s.Detail == "", "reset left player-facing state");
                Assert(s.CurrentBytesPerSecond == 0.0 && s.AverageBytesPerSecond == 0.0 && s.QueuePosition == 0, "reset left telemetry state");
                Console.WriteLine("  PASS");

                Console.WriteLine("PASS: Phase 7 synchronization UI state is deterministic and policy-free.");
                return 0;
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine("FAIL: "+ex);
                return 1;
            }
        }
    }
}
'@

try{
    Set-Content -LiteralPath $harnessPath -Value $harness -Encoding UTF8
    & $csc /nologo /langversion:5 /target:exe /out:$exePath $stateSource $harnessPath
    if($LASTEXITCODE-ne 0){throw "C# compiler failed with exit code $LASTEXITCODE"}
    & $exePath
    if($LASTEXITCODE-ne 0){throw "Phase 7 UI-state harness failed with exit code $LASTEXITCODE"}
}finally{
    if(-not$KeepSandbox){
        try{if(Test-Path -LiteralPath $sandbox){Remove-Item -LiteralPath $sandbox -Recurse -Force}}catch{}
    }else{
        Write-Host "Sandbox retained: $sandbox"
    }
}
