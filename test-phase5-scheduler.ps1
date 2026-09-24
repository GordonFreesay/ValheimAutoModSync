param(
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# AutoModSync 2.6 Phase 5 deterministic transfer-scheduler validation.
# Compiles the production scheduler source with a tiny isolated harness.
# It does not load Valheim, open sockets, touch BepInEx, or build release artifacts.

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'Source'
$schedulerSource = Join-Path $source 'AutoModSync.TransferScheduler.cs'
if (-not (Test-Path -LiteralPath $schedulerSource)) { throw "Missing source: $schedulerSource" }

$csc = $null
if (Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
elseif (Test-Path "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }

$sandbox = Join-Path $env:TEMP ('AMS26-Phase5-Scheduler-' + $PID)
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$harnessPath = Join-Path $sandbox 'SchedulerHarness.cs'
$exePath = Join-Path $sandbox 'SchedulerHarness.exe'

$harness = @'
using System;
using System.Collections.Generic;

namespace ValheimAutoModSync
{
    internal static class SchedulerHarness
    {
        private const long MiB = 1024L * 1024L;

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static int Main(string[] args)
        {
            try
            {
                Console.WriteLine("[1/8] Eight peers respect four active slots and FIFO queue positions...");
                AutoModSyncTransferScheduler slots = new AutoModSyncTransferScheduler(4, 4, 64L * MiB, (int)MiB, 0.25);
                for (long id = 1; id <= 8; id++) Assert(slots.Enqueue(id), "could not enqueue peer " + id);

                for (long expected = 1; expected <= 4; expected++)
                {
                    long actual;
                    Assert(slots.TryActivate(out actual), "active slot " + expected + " was not filled");
                    Assert(actual == expected, "FIFO activation order changed");
                }
                Assert(slots.ActiveCount == 4, "active slot limit was not four");
                Assert(slots.QueuedCount == 4, "queued count was not four");
                Assert(slots.QueuePosition(5) == 1 && slots.QueuePosition(8) == 4, "FIFO queue positions were wrong");
                long extra;
                Assert(!slots.TryActivate(out extra), "fifth peer bypassed MaxActiveBundleTransfers");
                Console.WriteLine("  PASS");

                Console.WriteLine("[2/8] Releasing a slot promotes exactly the oldest waiting peer...");
                slots.Remove(2);
                long promoted;
                Assert(slots.TryActivate(out promoted), "queued peer was not promoted");
                Assert(promoted == 5, "scheduler did not promote FIFO peer 5");
                Assert(slots.ActiveCount == 4, "active count did not return to four");
                Assert(slots.QueuePosition(6) == 1, "queue did not advance after promotion");
                Console.WriteLine("  PASS");

                Console.WriteLine("[3/8] Aggregate token bucket never exceeds configured rate plus bounded burst...");
                const long rate = 64L * MiB;
                const int grant = 1024 * 1024;
                const double burstSeconds = 0.25;
                AutoModSyncTransferScheduler bandwidth = new AutoModSyncTransferScheduler(4, 4, rate, grant, burstSeconds);
                for (long id = 1; id <= 4; id++)
                {
                    bandwidth.Enqueue(id);
                    long active;
                    Assert(bandwidth.TryActivate(out active), "could not activate bandwidth peer");
                    bandwidth.SetDemand(id, 2L * 1024L * MiB);
                }

                long startTicks = DateTime.UtcNow.Ticks;
                long sentTotal = 0L;
                Dictionary<long, long> sentByPeer = new Dictionary<long, long>();
                for (long id = 1; id <= 4; id++) sentByPeer[id] = 0L;

                const int steps = 250; // 5 seconds at 20 ms.
                for (int step = 0; step <= steps; step++)
                {
                    long nowTicks = startTicks + (long)(step * 0.020 * TimeSpan.TicksPerSecond);
                    int safety = 0;
                    while (safety++ < 1000)
                    {
                        long id;
                        int bytes;
                        if (!bandwidth.TryTakeGrant(nowTicks, out id, out bytes)) break;
                        sentTotal += bytes;
                        sentByPeer[id] += bytes;
                    }
                }

                double elapsedSeconds = steps * 0.020;
                long theoreticalMax = (long)Math.Ceiling(rate * (elapsedSeconds + burstSeconds)) + 4096;
                Assert(sentTotal <= theoreticalMax, "aggregate scheduler exceeded rate+burst envelope");
                Assert(sentTotal >= (long)(rate * elapsedSeconds * 0.95), "aggregate scheduler materially underused available budget in deterministic simulation");
                Console.WriteLine("  PASS");

                Console.WriteLine("[4/8] Round-robin grants keep four continuously-demanding peers fair...");
                long min = Int64.MaxValue;
                long max = 0L;
                for (long id = 1; id <= 4; id++)
                {
                    long value = sentByPeer[id];
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
                Assert(min > 0L, "one active peer was starved");
                Assert(max <= min + (2L * grant), "round-robin spread exceeded two grants");
                Assert((double)max / min < 1.10, "fastest/slowest deterministic share ratio exceeded 1.10");
                Console.WriteLine("  PASS");

                Console.WriteLine("[5/8] Refunding a blocked grant preserves bytes and advances fairness...");
                AutoModSyncTransferScheduler refund = new AutoModSyncTransferScheduler(2, 2, 8L * MiB, 512 * 1024, 0.25);
                refund.Enqueue(1);
                refund.Enqueue(2);
                Assert(refund.TryActivate(out promoted) && promoted == 1, "refund peer 1 activation failed");
                Assert(refund.TryActivate(out promoted) && promoted == 2, "refund peer 2 activation failed");
                refund.SetDemand(1, MiB);
                refund.SetDemand(2, MiB);

                long firstId;
                int firstGrant;
                Assert(refund.TryTakeGrant(startTicks, out firstId, out firstGrant), "first refund grant missing");
                Assert(firstId == 1, "round-robin did not begin with peer 1");
                long beforeRefundDemand = refund.DemandBytes(1);
                refund.RefundGrant(firstId, firstGrant);
                Assert(refund.DemandBytes(1) == beforeRefundDemand + firstGrant, "refund did not restore peer demand");

                long secondId;
                int secondGrant;
                Assert(refund.TryTakeGrant(startTicks, out secondId, out secondGrant), "second refund grant missing");
                Assert(secondId == 2, "blocked/refunded peer monopolized the next round-robin turn");
                Console.WriteLine("  PASS");

                Console.WriteLine("[6/8] Removed queued peers are skipped without corrupting later FIFO order...");
                AutoModSyncTransferScheduler removal = new AutoModSyncTransferScheduler(1, 2, 4L * MiB, 256 * 1024, 0.25);
                Assert(removal.Enqueue(10), "peer 10 enqueue failed");
                Assert(removal.Enqueue(11), "peer 11 enqueue failed");
                Assert(removal.Enqueue(12), "peer 12 enqueue failed");
                Assert(removal.TryActivate(out promoted) && promoted == 10, "peer 10 did not activate");
                removal.Remove(11);
                Assert(removal.QueuePosition(12) == 1, "removed queued peer still occupied a queue position");
                removal.Remove(10);
                Assert(removal.TryActivate(out promoted) && promoted == 12, "scheduler did not skip removed queued peer");
                Console.WriteLine("  PASS");

                Console.WriteLine("[7/8] Admission capacity is bounded at active + queued peers...");
                AutoModSyncTransferScheduler bounded = new AutoModSyncTransferScheduler(2, 3, 8L * MiB, 256 * 1024, 0.25);
                Assert(bounded.Enqueue(1), "bounded enqueue 1 failed");
                Assert(bounded.Enqueue(2), "bounded enqueue 2 failed");
                Assert(bounded.Enqueue(3), "bounded enqueue 3 failed");
                Assert(bounded.Enqueue(4), "bounded enqueue 4 failed");
                Assert(bounded.Enqueue(5), "bounded enqueue 5 failed");
                Assert(!bounded.Enqueue(6), "scheduler accepted a peer beyond active+queued capacity");
                Assert(bounded.MaxActive == 2 && bounded.MaxQueued == 3, "configured bounded capacity was not retained");
                Console.WriteLine("  PASS");

                Console.WriteLine("[8/8] Idle expiry never fires while scheduler demand is outstanding...");
                long nowTicks = DateTime.UtcNow.Ticks;
                long oldTicks = nowTicks - 30L * TimeSpan.TicksPerSecond;
                Assert(!AutoModSyncTransferScheduler.ShouldExpireActivePeer(oldTicks, nowTicks, 10, true), "outstanding demand was incorrectly expired");
                Assert(AutoModSyncTransferScheduler.ShouldExpireActivePeer(oldTicks, nowTicks, 10, false), "idle no-demand peer was not expired");
                Assert(!AutoModSyncTransferScheduler.ShouldExpireActivePeer(nowTicks, nowTicks, 10, false), "fresh activity was incorrectly expired");
                Console.WriteLine("  PASS");

                Console.WriteLine();
                Console.WriteLine("PASS: all Phase 5 deterministic transfer-scheduler checks passed.");
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

& $csc /nologo /optimize+ /langversion:5 /target:exe /out:$exePath $harnessPath $schedulerSource
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Phase 5 scheduler harness compilation failed. Sandbox retained: $sandbox"
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
