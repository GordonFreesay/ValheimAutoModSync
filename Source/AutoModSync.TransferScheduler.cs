using System;
using System.Collections.Generic;

namespace ValheimAutoModSync
{
    // Intent: Pure server-wide admission/fairness/token-bucket scheduler for AutoModSync bundle transfers.
    // Scope: this class knows only opaque peer ids and raw payload-byte demand. ZRpc, Steam queue backpressure,
    // bundle construction, disk I/O, and wire framing remain in ServerPlugin so this policy can be tested deterministically.
    internal sealed class AutoModSyncTransferScheduler
    {
        private sealed class PeerState
        {
            internal long Id;
            internal bool Active;
            internal long DemandBytes;
        }

        private readonly Dictionary<long, PeerState> _peers = new Dictionary<long, PeerState>();
        private readonly Queue<long> _waiting = new Queue<long>();
        private readonly List<long> _active = new List<long>();
        private int _roundRobinCursor;
        private int _maxActive;
        private long _aggregateBytesPerSecond;
        private int _maxGrantBytes;
        private double _burstSeconds;
        private double _tokenCapacity;
        private double _tokens;
        private long _lastRefillUtcTicks;

        // Intent: Creates one bounded scheduler with an explicit active-slot limit, aggregate raw-byte budget, grant size, and short burst envelope.
        internal AutoModSyncTransferScheduler(int maxActive, long aggregateBytesPerSecond, int maxGrantBytes, double burstSeconds)
        {
            Configure(maxActive, aggregateBytesPerSecond, maxGrantBytes, burstSeconds);
        }

        // Intent: Applies server configuration while preserving no more than the newly allowed burst of accumulated tokens.
        internal void Configure(int maxActive, long aggregateBytesPerSecond, int maxGrantBytes, double burstSeconds)
        {
            _maxActive = Math.Max(1, maxActive);
            _aggregateBytesPerSecond = Math.Max(1L, aggregateBytesPerSecond);
            _maxGrantBytes = Math.Max(4096, maxGrantBytes);
            _burstSeconds = Math.Max(0.05, Math.Min(2.0, burstSeconds));
            _tokenCapacity = Math.Max((double)_maxGrantBytes, _aggregateBytesPerSecond * _burstSeconds);
            if (_lastRefillUtcTicks == 0L) _tokens = _tokenCapacity;
            else _tokens = Math.Min(_tokens, _tokenCapacity);
        }

        // Intent: Adds a peer to the FIFO admission queue exactly once; an already-active or already-waiting peer is unchanged.
        internal bool Enqueue(long peerId)
        {
            if (peerId <= 0L) throw new ArgumentOutOfRangeException("peerId");
            if (_peers.ContainsKey(peerId)) return false;

            PeerState state = new PeerState();
            state.Id = peerId;
            _peers.Add(peerId, state);
            _waiting.Enqueue(peerId);
            return true;
        }

        // Intent: Promotes the oldest still-waiting peer when an active transfer slot is available.
        internal bool TryActivate(out long peerId)
        {
            peerId = 0L;
            if (_active.Count >= _maxActive) return false;

            while (_waiting.Count > 0)
            {
                long id = _waiting.Dequeue();
                PeerState state;
                if (!_peers.TryGetValue(id, out state) || state.Active) continue;

                state.Active = true;
                state.DemandBytes = 0L;
                _active.Add(id);
                peerId = id;
                return true;
            }
            return false;
        }

        // Intent: Records the exact raw payload bytes represented by one outstanding client request.
        // Contract: one peer has at most one outstanding chunk-window request, so replacing nonzero demand is rejected.
        internal void SetDemand(long peerId, long bytes)
        {
            if (bytes <= 0L) throw new ArgumentOutOfRangeException("bytes");
            PeerState state;
            if (!_peers.TryGetValue(peerId, out state) || !state.Active)
                throw new InvalidOperationException("AutoModSync scheduler demand requires an active peer.");
            if (state.DemandBytes != 0L)
                throw new InvalidOperationException("AutoModSync scheduler peer already has outstanding demand.");
            state.DemandBytes = bytes;
        }

        // Intent: Selects at most one bounded raw-byte grant in round-robin order and charges it against the aggregate token bucket.
        // Fairness: every successful selection advances the cursor; peers with no outstanding demand are skipped without losing their active slot.
        internal bool TryTakeGrant(long nowUtcTicks, out long peerId, out int grantBytes)
        {
            peerId = 0L;
            grantBytes = 0;
            Refill(nowUtcTicks);
            if (_active.Count == 0 || _tokens < 1.0) return false;

            if (_roundRobinCursor < 0 || _roundRobinCursor >= _active.Count) _roundRobinCursor = 0;
            int checkedCount;
            for (checkedCount = 0; checkedCount < _active.Count; checkedCount++)
            {
                if (_roundRobinCursor >= _active.Count) _roundRobinCursor = 0;
                long id = _active[_roundRobinCursor];

                PeerState state;
                if (!_peers.TryGetValue(id, out state) || !state.Active || state.DemandBytes <= 0L)
                {
                    _roundRobinCursor = (_roundRobinCursor + 1) % Math.Max(1, _active.Count);
                    continue;
                }

                long available = (long)Math.Floor(_tokens);
                long grant = Math.Min(state.DemandBytes, (long)_maxGrantBytes);
                // Fixed-size grants avoid turning each refill remainder into a tiny extra turn for the next peer.
                // The only smaller grant is the peer's final outstanding demand. If the bucket is short,
                // keep the cursor on this peer so refill boundaries cannot silently skip its turn.
                if (grant <= 0L || available < grant) return false;

                _roundRobinCursor = (_roundRobinCursor + 1) % Math.Max(1, _active.Count);
                state.DemandBytes -= grant;
                _tokens -= grant;
                peerId = id;
                grantBytes = (int)grant;
                return true;
            }

            return false;
        }

        // Intent: Returns unused bytes from a reserved grant when chunk boundaries or Steam backpressure prevent sending the full reservation.
        internal void RefundGrant(long peerId, int bytes)
        {
            if (bytes <= 0) return;
            PeerState state;
            if (_peers.TryGetValue(peerId, out state) && state.Active)
                state.DemandBytes += bytes;
            _tokens = Math.Min(_tokenCapacity, _tokens + bytes);
        }

        // Intent: Removes a completed/disconnected peer from active or waiting state so a queued peer can be promoted.
        internal void Remove(long peerId)
        {
            PeerState state;
            if (!_peers.TryGetValue(peerId, out state)) return;

            if (state.Active)
            {
                int index = _active.IndexOf(peerId);
                if (index >= 0)
                {
                    _active.RemoveAt(index);
                    if (_active.Count == 0) _roundRobinCursor = 0;
                    else
                    {
                        if (index < _roundRobinCursor) _roundRobinCursor--;
                        if (_roundRobinCursor >= _active.Count) _roundRobinCursor = 0;
                    }
                }
            }

            _peers.Remove(peerId);
            // Waiting entries are lazily skipped by TryActivate/QueuePosition so removal stays O(active peers).
        }

        // Intent: Returns the current 1-based FIFO position among still-waiting peers, or zero when the peer is active/not queued.
        internal int QueuePosition(long peerId)
        {
            PeerState target;
            if (!_peers.TryGetValue(peerId, out target) || target.Active) return 0;

            int position = 0;
            long[] ids = _waiting.ToArray();
            int i;
            for (i = 0; i < ids.Length; i++)
            {
                PeerState state;
                if (!_peers.TryGetValue(ids[i], out state) || state.Active) continue;
                position++;
                if (ids[i] == peerId) return position;
            }
            return 0;
        }

        // Intent: Reports whether the peer currently owns one active transfer slot.
        internal bool IsActive(long peerId)
        {
            PeerState state;
            return _peers.TryGetValue(peerId, out state) && state.Active;
        }

        // Intent: Reports exact remaining raw-byte demand for deterministic tests and server diagnostics.
        internal long DemandBytes(long peerId)
        {
            PeerState state;
            return _peers.TryGetValue(peerId, out state) ? state.DemandBytes : 0L;
        }

        // Intent: Exposes current active-slot occupancy for queue UI and telemetry.
        internal int ActiveCount { get { return _active.Count; } }

        // Intent: Counts live waiting peers while ignoring lazily-retained queue entries for removed peers.
        internal int QueuedCount
        {
            get
            {
                int count = 0;
                long[] ids = _waiting.ToArray();
                int i;
                for (i = 0; i < ids.Length; i++)
                {
                    PeerState state;
                    if (_peers.TryGetValue(ids[i], out state) && !state.Active) count++;
                }
                return count;
            }
        }

        // Intent: Exposes the configured active transfer limit for queue status messages.
        internal int MaxActive { get { return _maxActive; } }

        // Intent: Refills the aggregate token bucket according to elapsed UTC ticks while bounding burst accumulation.
        private void Refill(long nowUtcTicks)
        {
            if (nowUtcTicks <= 0L) return;
            if (_lastRefillUtcTicks == 0L)
            {
                _lastRefillUtcTicks = nowUtcTicks;
                _tokens = Math.Min(_tokens, _tokenCapacity);
                return;
            }
            if (nowUtcTicks <= _lastRefillUtcTicks) return;

            double elapsed = (nowUtcTicks - _lastRefillUtcTicks) / (double)TimeSpan.TicksPerSecond;
            _lastRefillUtcTicks = nowUtcTicks;
            _tokens = Math.Min(_tokenCapacity, _tokens + elapsed * _aggregateBytesPerSecond);
        }
    }
}
