using System;

namespace ValheimAutoModSync
{
    internal enum AutoModSyncUiPhase
    {
        Hidden = 0,
        Discovering = 1,
        Checking = 2,
        Trust = 3,
        Comparing = 4,
        Queued = 5,
        Downloading = 6,
        Verifying = 7,
        Applying = 8,
        Restarting = 9,
        Reconnecting = 10,
        Complete = 11,
        Failed = 12
    }

    // Intent: Holds player-facing synchronization state and transfer math without owning networking, trust, filesystem, or rendering policy.
    // Phase 7 keeps this model deterministic so the UI can display the same state that production synchronization code has already decided.
    internal sealed class AutoModSyncUiState
    {
        public AutoModSyncUiPhase Phase { get; private set; }
        public string Status { get; private set; }
        public string Detail { get; private set; }
        public string ServerFingerprint { get; private set; }

        public int ManifestFiles { get; private set; }
        public int MatchedFiles { get; private set; }
        public int ChangedFiles { get; private set; }
        public int RemovedFiles { get; private set; }
        public long RequiredExpandedBytes { get; private set; }

        public int QueuePosition { get; private set; }
        public int QueueActive { get; private set; }
        public int QueueMaxActive { get; private set; }

        public long BundleBytes { get; private set; }
        public long BytesReceived { get; private set; }
        public long SessionStartBytes { get; private set; }
        public double CurrentBytesPerSecond { get; private set; }
        public double AverageBytesPerSecond { get; private set; }

        public int VerificationTotal { get; private set; }
        public int VerificationCompleted { get; private set; }

        private DateTime _transferStartedUtc;
        private DateTime _lastRateSampleUtc;
        private long _lastRateSampleBytes;

        // Intent: Initializes a fresh synchronization presentation state with no stale server identity, counters, or transfer metrics.
        public AutoModSyncUiState()
        {
            Reset();
        }

        // Intent: Clears all presentation state between protected sessions so no previous server's progress or identity can leak into a later join.
        public void Reset()
        {
            Phase = AutoModSyncUiPhase.Hidden;
            Status = "";
            Detail = "";
            ServerFingerprint = "";
            ManifestFiles = 0;
            MatchedFiles = 0;
            ChangedFiles = 0;
            RemovedFiles = 0;
            RequiredExpandedBytes = 0L;
            QueuePosition = 0;
            QueueActive = 0;
            QueueMaxActive = 0;
            BundleBytes = 0L;
            BytesReceived = 0L;
            SessionStartBytes = 0L;
            CurrentBytesPerSecond = 0.0;
            AverageBytesPerSecond = 0.0;
            VerificationTotal = 0;
            VerificationCompleted = 0;
            _transferStartedUtc = DateTime.MinValue;
            _lastRateSampleUtc = DateTime.MinValue;
            _lastRateSampleBytes = 0L;
        }

        // Intent: Records a synchronization phase plus already-decided player-facing wording; callers remain responsible for all actual policy/actions.
        public void SetPhase(AutoModSyncUiPhase phase, string status, string detail)
        {
            Phase = phase;
            Status = status ?? "";
            Detail = detail ?? "";
        }

        // Intent: Binds the verified server fingerprint to trust/status presentation without making or persisting a trust decision.
        public void SetServerFingerprint(string fingerprint)
        {
            ServerFingerprint = fingerprint ?? "";
        }

        // Intent: Records the result of the signed-manifest comparison for transparent matched/changed/removal/byte counts.
        public void SetComparison(int manifestFiles, int matchedFiles, int changedFiles, int removedFiles, long requiredExpandedBytes)
        {
            ManifestFiles = Math.Max(0, manifestFiles);
            MatchedFiles = Math.Max(0, matchedFiles);
            ChangedFiles = Math.Max(0, changedFiles);
            RemovedFiles = Math.Max(0, removedFiles);
            RequiredExpandedBytes = Math.Max(0L, requiredExpandedBytes);
        }

        // Intent: Records server scheduler position for display only; queue admission and fairness remain entirely server-owned.
        public void SetQueue(int position, int active, int maxActive)
        {
            QueuePosition = Math.Max(0, position);
            QueueActive = Math.Max(0, active);
            QueueMaxActive = Math.Max(0, maxActive);
        }

        // Intent: Starts transfer telemetry from an optional verified resume prefix and establishes the baseline for rate/ETA calculations.
        public void BeginTransfer(long bundleBytes, long initialBytes, DateTime nowUtc)
        {
            BundleBytes = Math.Max(0L, bundleBytes);
            BytesReceived = Math.Max(0L, Math.Min(BundleBytes, initialBytes));
            SessionStartBytes = BytesReceived;
            CurrentBytesPerSecond = 0.0;
            AverageBytesPerSecond = 0.0;
            _transferStartedUtc = nowUtc;
            _lastRateSampleUtc = nowUtc;
            _lastRateSampleBytes = BytesReceived;
        }

        // Intent: Updates current/average throughput from monotonic received-byte observations; it never controls transfer pacing.
        public void UpdateTransfer(long receivedBytes, DateTime nowUtc)
        {
            long bounded = Math.Max(0L, BundleBytes > 0L ? Math.Min(BundleBytes, receivedBytes) : receivedBytes);
            if (bounded < BytesReceived) bounded = BytesReceived;
            BytesReceived = bounded;

            if (_transferStartedUtc == DateTime.MinValue)
                _transferStartedUtc = nowUtc;

            double elapsed = Math.Max(0.001, (nowUtc - _transferStartedUtc).TotalSeconds);
            long sessionBytes = Math.Max(0L, BytesReceived - SessionStartBytes);
            AverageBytesPerSecond = sessionBytes / elapsed;

            if (_lastRateSampleUtc == DateTime.MinValue)
            {
                _lastRateSampleUtc = nowUtc;
                _lastRateSampleBytes = BytesReceived;
                return;
            }

            double sampleSeconds = (nowUtc - _lastRateSampleUtc).TotalSeconds;
            if (sampleSeconds < 0.25) return;

            long sampleBytes = Math.Max(0L, BytesReceived - _lastRateSampleBytes);
            double instant = sampleBytes / Math.Max(0.001, sampleSeconds);
            CurrentBytesPerSecond = CurrentBytesPerSecond <= 0.0 ? instant : (CurrentBytesPerSecond * 0.65) + (instant * 0.35);
            _lastRateSampleUtc = nowUtc;
            _lastRateSampleBytes = BytesReceived;
        }

        // Intent: Starts per-file verification progress after the complete compressed artifact has passed its own SHA-256 check.
        public void BeginVerification(int totalFiles)
        {
            VerificationTotal = Math.Max(0, totalFiles);
            VerificationCompleted = 0;
        }

        // Intent: Advances verification only after one extracted staging file has passed its signed size/SHA-256 checks.
        public void MarkVerified()
        {
            if (VerificationCompleted < VerificationTotal)
                VerificationCompleted++;
        }

        // Intent: Returns normalized download progress for rendering only.
        public double TransferProgress()
        {
            if (BundleBytes <= 0L) return 0.0;
            return Math.Max(0.0, Math.Min(1.0, BytesReceived / (double)BundleBytes));
        }

        // Intent: Returns session transfer elapsed time for player-facing telemetry.
        public double TransferElapsedSeconds(DateTime nowUtc)
        {
            if (_transferStartedUtc == DateTime.MinValue) return 0.0;
            return Math.Max(0.0, (nowUtc - _transferStartedUtc).TotalSeconds);
        }

        // Intent: Estimates remaining download time from measured rate; unknown/nonpositive rates deliberately report -1 instead of inventing an ETA.
        public double TransferEtaSeconds()
        {
            long remaining = Math.Max(0L, BundleBytes - BytesReceived);
            if (remaining == 0L) return 0.0;
            double rate = CurrentBytesPerSecond > 1.0 ? CurrentBytesPerSecond : AverageBytesPerSecond;
            if (rate <= 1.0) return -1.0;
            return remaining / rate;
        }
    }
}
