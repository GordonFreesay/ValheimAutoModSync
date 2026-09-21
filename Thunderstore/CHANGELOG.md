# Changelog

## 2.5.0

- Moves AutoModSync preflight ahead of the normal Valheim ServerHandshake so Jotunn/Epic Loot and similar mod-compatibility checks run only after synchronization has had a chance to complete.
- Holds/replays only Valheim's own ServerHandshake; third-party compatibility results are not bypassed or rewritten.
- Preserves and replays the exact original `ServerHandshake` argument list instead of synthesizing an empty call, preventing malformed-handshake `EndOfStreamException` failures on current Valheim builds.
- Adds an optional AMS4 preflight acknowledgement before potentially expensive manifest generation.
- Speeds up first-time/bare-client synchronization with a backward-compatible windowed bundle transfer: 2.5 peers can deliver up to 16 ordered chunks per request while legacy AMS4 peers keep the original one-chunk pull behavior.
- Opens/seeks the prepared bundle ZIP once per transfer window instead of once for every ~24 KiB chunk.
- Adds a faster `bundle-batch1` path that packs up to ~384 KiB of raw compressed bundle bytes into one RPC, eliminating Base64 expansion and most per-chunk RPC message overhead while keeping the older windowed/single-chunk AMS4 fallbacks.
- Logs measured bundle transfer size, elapsed time, and MiB/s after verification so throughput regressions are visible in normal client logs.
- Works around Valheim's ~153600 B/s SteamNetworkingSockets send-rate ceiling during bundle delivery by temporarily raising only `SendRateMax` on the specific Steam connection, then restoring the previous value after success/failure. `SendRateMin` is never raised.
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