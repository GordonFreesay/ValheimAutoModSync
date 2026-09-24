# Changelog

## 2.6.0 (development)

- Added a server-wide concurrent bundle scheduler with FIFO active slots, round-robin aggregate bandwidth grants, optional client queue-position status, Steam reliable-queue backpressure, per-transfer persistent ZIP streams, and dead/idle slot cleanup. Defaults are four active transfers and a 64 MiB/s aggregate raw-payload budget.
- Added deterministic eight-peer scheduler validation and Windows CI. The first CI run exposed a refill-boundary fairness bug; the scheduler now preserves a peer's turn when tokens are temporarily insufficient and the corrected 6/6 harness passes.

- Added exact-artifact resumable bundle downloads as an optional AMS4 `bundle-resume1` capability. Interrupted clients retain one bounded partial ZIP, and the server independently verifies the exact retained prefix against the current immutable artifact before allowing a nonzero resume offset; mismatches safely restart from zero.
- Added development-only single-client transfer interruption emulation and an isolated deterministic Phase 4 resume harness. These validation hooks are not compiled into release client binaries.

- Development work is isolated on `dev/2.6`; the current public release remains 2.5.0 until the 2.6 validation gates are complete.
- 2.6 preserves AMS4/protocol 4 as the compatibility baseline and will add new behavior through negotiated capabilities where possible.
- Release packaging/publication is intentionally deferred until functional validation is completed.
- Phase 1 separates non-AMS discovery fail-open behavior from recognized-AMS fail-closed behavior: signature, trust, path, resource, transfer, and apply-preparation failures now abort that join instead of replaying the vanilla handshake.
- Establishes server fingerprint trust after a valid signed manifest even when the client already has matching files.
- Adds shared Windows path hardening for client/server/apply: reserved device names, trailing dot/space aliases, invalid/control characters, fixed-root containment, and reparse-point rejection.
- Adds independent client compressed/expanded/per-file/file-count/chunk-count ceilings and streaming ZIP extraction bounds.
- Adds server `MaxExpandedBundleMiB` and checks compressed output while constructing the bundle instead of waiting only for the finished ZIP.
- Replaces destructive per-file apply with a durable transactional helper: old destinations are backed up before a `PREPARED` marker, the complete new set is verified before `COMMITTED`, pre-commit interruption rolls back/retries from retained staging, and post-commit interruption preserves the new set and only finishes cleanup.
- Client startup no longer copies leftover staging into live plugin/config roots; unfinished pending/journal state is handed back to the out-of-process helper before AMS can attempt a server join.

## 2.5.0

- Moves AutoModSync preflight ahead of the normal Valheim ServerHandshake so Jotunn/Epic Loot and similar mod-compatibility checks run only after synchronization has had a chance to complete.
- Holds/replays only Valheim's own ServerHandshake; third-party compatibility results are not bypassed or rewritten.
- Preserves and replays the exact original `ServerHandshake` argument list instead of synthesizing an empty call, preventing malformed-handshake `EndOfStreamException` failures on current Valheim builds.
- Adds an optional AMS4 preflight acknowledgement before potentially expensive manifest generation.
- Speeds up first-time/bare-client synchronization with a backward-compatible windowed bundle transfer: 2.5 peers can deliver up to 16 ordered chunks per request while legacy AMS4 peers keep the original one-chunk pull behavior.
- Opens/seeks the prepared bundle ZIP once per transfer window instead of once for every ~24 KiB chunk.
- Adds a faster `bundle-batch1` path that packs up to ~384 KiB of raw compressed bundle bytes into one RPC, eliminating Base64 expansion and most per-chunk RPC message overhead while keeping the older windowed/single-chunk AMS4 fallbacks.
- Logs measured bundle transfer size, elapsed time, and MiB/s after verification so throughput regressions are visible in normal client logs.
- Works around Valheim's ~153600 B/s SteamNetworkingSockets transfer floor/ceiling during bundle delivery by tuning the **live ZRpc Steam connection directly**: temporarily raises its send-rate maximum, a bounded send-rate minimum, and reliable send buffer, then restores the exact previous values after success/failure. This avoids relying on the pre-handshake peer already being visible in `ZNet.GetPeers()`.
- Live telemetry showed Steam holding the bundle connection exactly at the configured 1 MiB/s minimum for the full transfer. The 2.5.0 defaults use an 8 MiB/s temporary minimum, 32 MiB/s maximum, and 16 MiB reliable buffer, with automatic migration from the earlier exact 2.5 test defaults.
- Adds `bundle-pipeline1`: current peers can request up to 128 chunks per window while the server emits multiple <=384 KiB binary messages back-to-back. This keeps several MiB in flight without exceeding the conservative per-message size used by `bundle-batch1`.
- Hardens the pre-handshake discovery path: the client now retries `AMS4_Hello` several times inside the existing short fail-open window, covering the race where the first custom RPC arrives before the dedicated server has finished registering handlers. Server startup also installs the early connection hook before nonessential development-config migration and logs handler/hello milestones for diagnosis.
- Extends the signed manifest/install pipeline beyond `BepInEx/plugins`: preloader files under `BepInEx/patchers` can now be synchronized to their real patcher root, and selected `BepInEx/config` files can be synchronized through an explicit server allowlist.
- Adds `ServerOnlyPatterns` and optional `ClientRequiredPatterns` compatibility classification so dedicated-server-only files do not have to be advertised to clients.
- Keeps config synchronization opt-in and hard-blocks the AutoModSync signing identity files and loader-wide `BepInEx.cfg` from ever entering the synchronized config manifest.
- Keeps protocol version 4 / AMS4 for backward compatibility with 2.4.x peers.
- Adds Authenticode signing/verification support for AutoModSync-authored release binaries.
- Adds source-level intent/workflow documentation above every C# function.

## 2.4.8

- Fixes Thunderstore/r2modman profile state-root handling during synchronization.
- Saves the original package-manager launch context before restart.
- Relaunches Valheim through Steam with the original Doorstop/BepInEx profile arguments so the replacement process remains modded.
- Restores automatic one-shot reconnect to the server after synchronized files are applied.
- Automatically continues through the previously selected character during reconnect.
- Prevents duplicate reconnect dispatches and false reconnect timeout warnings.
- Standalone/manual installation behavior and protocol version 4 remain unchanged.
## 2.4.7

- Native Thunderstore/r2modman installation from the same source used by the standalone release.
- Client role disables itself on dedicated-server processes.
- Apply helper supports package-manager installation paths while keeping persistent state under `BepInEx/AutoModSync`.
- Server signing identity is generated automatically on first launch when missing.
- Package-managed clients ignore AutoModSync-owned files advertised by older standalone servers, preventing downgrade/duplicate copies.
- Package-managed servers do not advertise stale standalone release-client payloads.
- Signed manifests, SHA-256 verification, delta synchronization, restart/reconnect, and protocol version 4 are preserved.