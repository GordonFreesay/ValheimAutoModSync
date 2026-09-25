# Changelog

## 2.6.0

- Adds content-addressed bundle caching, startup prewarming, and single-flight construction so identical fresh-client requests reuse one immutable verified ZIP rather than rebuilding it per client.
- Adds a bounded FIFO bundle scheduler with configurable active/queued limits, round-robin aggregate bandwidth grants, Steam reliable-queue backpressure, persistent per-transfer streams, queue-position telemetry, and dead/idle cleanup.
- Adds exact-artifact resumable downloads through the optional AMS4 `bundle-resume1` capability. Interrupted clients retain one bounded partial ZIP; the server independently verifies the exact retained prefix against the current immutable artifact before accepting a nonzero resume point.
- Adds `BepInEx/AutoModSync/ClientPayload/plugins/**` for client-required files/assets that dedicated servers should distribute without loading themselves.
- Adds conservative fingerprint-scoped ownership. AutoModSync claims only files it actually installs/replaces, does not claim matching pre-existing local files, and retires stale files only when the live bytes still exactly match the same trusted server's last-owned SHA-256/size.
- Adds same-server continuity requirements for stale deletion so switching trusted servers cannot cause one server's ownership ledger to delete another server's files.
- Replaces destructive per-file apply with a durable transactional helper. PREPARED transactions back up old destinations and roll back/retry after interruption; COMMITTED transactions preserve the complete new state and finish cleanup.
- Adds strict client/server resource ceilings, bounded streaming ZIP extraction, fixed-root path containment, Windows reserved-name/trailing-dot-space defenses, and reparse-point rejection across client/server/apply.
- Separates non-AMS fail-open discovery from recognized-AMS fail-closed behavior. Once a server positively identifies as AMS, signature/trust/path/resource/transfer/apply-preparation failures abort that protected join instead of silently bypassing synchronization.
- Adds the state-driven AMS synchronization panel with signed-manifest comparison counts, queue state, download progress, current/average throughput, ETA, retained resume bytes, verification progress, apply/restart/reconnect status, and bounded failure presentation.
- Replaces normal full-fingerprint display with a short first-contact human-comparison security code. The complete 256-bit fingerprint is still used internally for signature verification, trust pinning, server-change detection, and ownership scoping.
- Adds passive Steam server-browser presence rules and a small client-side AMS badge. Badge binding uses Valheim's exact `m_serverListElements` ownership rather than index pairing, preventing pooled/reused rows from inheriting another server's badge. Server advertisement and client badge display are independently configurable.
- Polishes the standalone installer with AMS charcoal/orange branding, embedded icons, complete/partial install detection, Repair / Update, and role-aware Uninstall.
- Client uninstall removes synchronized files only when strict ownership metadata plus live size/SHA-256 prove the bytes are still AMS-owned; locally modified files, unrelated mods, and shared BepInEx are preserved.
- Server uninstall preserves operator-managed `ClientPayload`, server config, and signing identity by default. Explicit identity removal deletes only the documented AMS server config/private/public identity files while preserving shared BepInEx and operator payloads.
- Adds first-party PII guards to development/release builds and Windows CI for authored source/text plus printable strings in AutoModSync-authored binaries.
- Adds reproducible AMS executable branding from the canonical PNG, including multi-size ICO output for the Apply helper and installer.
- Adds SHA-256 release manifests plus GitHub/Sigstore build-provenance attestations for official GitHub-built standalone and store packages. Users can verify exact downloaded bytes with `gh attestation verify <artifact> -R GordonFreesay/ValheimAutoModSync`; this is separate from Windows Authenticode/SmartScreen trust.
- Keeps AMS4 / protocol 4 and preserves negotiated compatibility fallbacks for older AMS4 peers.

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