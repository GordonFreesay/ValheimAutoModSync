# Source walkthrough

This document is a map of the AutoModSync source tree for reviewers, contributors, and automated code-analysis tools. The code files themselves also contain an `Intent:` comment above every function.

## Trust boundaries

AutoModSync has three distinct trust boundaries:

1. **Server identity** — each server owns an RSA keypair. The server signs the exact manifest text; the client derives and pins the public-key fingerprint on first trust.
2. **Transferred content** — the client requests only missing/changed manifest entries, verifies the bundle hash/size, then verifies every extracted file against the signed manifest before staging it.
3. **Filesystem containment** — manifest, ZIP, staging, server-source, and apply-helper paths are normalized beneath fixed BepInEx/AutoModSync roots. Parent/rooted paths, Windows reserved device names, trailing-dot/space aliases, control/invalid characters, and filesystem reparse-point redirection are rejected.

Signing the AutoModSync release itself is separate from the server-manifest signature. Authenticode identifies the publisher of AutoModSync's own binaries; the server RSA identity authenticates what a trusted game server advertised.

## Main source files

### Source/ValheimAutoModSync.Client.cs

The in-game client plugin owns connection preflight, server trust, download verification, staging, restart/reconnect state, and the small synchronization UI.

Current `dev/2.6` connection sequence:

```text
Valheim creates outgoing ZNet connection
  -> AutoModSync OnNewConnection prefix registers AMS RPCs
  -> preflight gate is armed
  -> vanilla ZRpc.Invoke("ServerHandshake") is held
  -> OnNewConnection postfix sends AMS4_Hello
     -> no AMS response: fail open and replay ServerHandshake
     -> AMS4_Ack: recognized AMS session; wait for signed manifest
     -> verify signed manifest
     -> establish/check server fingerprint trust even when files already match
     -> manifest matches: replay ServerHandshake
     -> files differ:
          enforce client file/expanded/compressed hard limits
          request exact changed-file bundle
          verify package + every file
          stage files
          persist reconnect/launch context
          start apply helper
          quit Valheim
```

After a successful/matching preflight, AutoModSync stops gating and Valheim plus other mods continue their normal handshakes. Jotunn, ServerSync-style mods, and similar validators are not patched or told to ignore mismatches; they see the synchronized client after preflight.

First-contact trust uses the native Windows Yes/No fingerprint dialog, but 2.6 shows it from a background STA thread rather than Unity's main thread. The signed manifest and exact active `ZRpc` remain bound to the pending decision while Valheim's normal networking loop continues. AutoModSync deliberately does not change `Cursor.visible` or `Cursor.lockState`; Windows owns pointer interaction for the native dialog, avoiding the cursor tug-of-war seen with the earlier in-game-button experiment. Trust acceptance is valid only for the same still-connected, signature-verified AMS session. A generation token prevents a result from an old dialog being applied to a later connection, and connection-loss cleanup best-effort closes any still-open trust window. Recognized-session failure remains fail-closed and never replays the held vanilla handshake. A blocked-join status banner is transient and self-clears after roughly four seconds instead of remaining over the main menu.

The older SendPeerInfo AMS4 probe remains as a fallback for connection paths that do not pass through the early gate. Keeping the same AMS4 protocol preserves compatibility with existing 2.4.x AutoModSync peers.

For large first-time synchronizations, 2.5.0 negotiates optional transfer capabilities through `AMS4_Ack`. `bundle-batch1` packs up to roughly 384 KiB of raw ZIP data into one `AMS4_BundleBatch` RPC, avoiding Base64 expansion. Current peers prefer `bundle-pipeline1`: the client requests up to 128 chunks at once and the server emits multiple bounded batch RPCs back-to-back, keeping several MiB queued while preserving the same <=384 KiB per-message bound. If pipelining is unavailable, the client falls back to one binary batch request at a time, then to `bundle-window1`, then to the original one-chunk AMS4 behavior.

For Steam-backed dedicated-server peers, the server resolves the transport directly from the active `ZRpc` (rather than depending on an early pre-handshake peer already appearing in `ZNet.GetPeers()`). During the bundle it temporarily tunes that exact Steam connection's send-rate maximum, bounded send-rate minimum, and reliable send buffer, then restores the exact previous values it changed. This keeps the acceleration scoped to synchronization instead of permanently rewriting gameplay networking.

The client hello also advertises `roots1`. That capability means the client/apply helper understand the fixed `P`/ `R`/ `C` destinations. A server only requires it when its signed manifest actually contains patcher or config entries, so ordinary plugin-only AMS4 compatibility remains available.

### Source/ValheimAutoModSync.Server.cs

The server plugin registers AMS4 RPCs on incoming Valheim connections and serves a deterministic signed view of eligible files from fixed BepInEx roots: plugins (`P`), patchers (`R`), and explicitly allowlisted config (`C`). Server-only/client-required pattern rules are applied before the canonical manifest is signed.

2.5.0 adds an optional `AMS4_Ack` immediately after a valid hello and before manifest hashing. New clients use that acknowledgement to distinguish a slow manifest build from a non-AutoModSync server. Older clients ignore the unknown acknowledgement and continue to understand the existing AMS4 manifest messages.

The server never opens a second listener or contacts an external download service.

On `dev/2.6`, bundle construction is content-addressed. The server sorts the exact requested signed records, hashes kind/path/size/content-hash metadata into a bundle cache key, builds a ZIP privately, hashes the completed ZIP, and only then publishes it as an immutable artifact. Identical clients reuse that artifact for `BundleCacheSeconds` instead of recompressing the same files. If identical requests arrive while the artifact is still being built, they join one single-flight build and consume the same published ZIP afterward. Active transfers reference-count the artifact so cache cleanup cannot delete a ZIP another client is reading; idle artifacts are TTL/LRU-evicted under `BundleCacheMaxMiB`. Cache metadata is process-local and startup deletes orphaned ZIP/temp files rather than trusting artifacts from a previous process.

Dedicated servers additionally prewarm the dominant public-server case during initial plugin startup when `PrebuildFreshClientBundle=true`: every signed distributable record except `ValheimAutoModSync.Client.dll`, because a client must already have the AutoModSync client plugin in order to request synchronization. That startup artifact is pinned against TTL expiry until its first real client hit (the disk-budget policy may still evict it), so an otherwise idle public server does not lose the prewarm merely because no player joins within the ordinary cache TTL. After the first live hit, normal TTL/LRU behavior resumes. Clients with a different partial/delta set still select their own exact content key and build lazily on the first such request.

Bundle-build telemetry distinguishes `MISS`, `HIT`, and `WAIT-HIT` and records ZIP construction, final ZIP SHA-256, total preparation, and any single-flight wait time. While copying source bytes into a new artifact, the server simultaneously re-hashes each source against the signed manifest record so a same-size local change cannot be published under an obsolete cache key.

Recursive manifest scanning does not traverse reparse-point files/directories. Bundle requests also have a pre-compression expanded-size ceiling (`MaxExpandedBundleMiB`) in addition to the existing individual-file and compressed-bundle ceilings. The client independently caps incoming compressed bytes, expanded synchronized bytes, file count, per-file bytes, chunk count, and streaming ZIP extraction.

### Source/ValheimAutoModSync.Installer.cs

The standalone GUI installer is the primary manual-distribution entry point for 2.5.0. It requires administrator elevation through its embedded Windows manifest because Steam installations commonly live under Program Files.

It performs no network downloads. The release builder downloads the pinned BepInEx archive once at build time, verifies its fixed SHA-256, and places that archive under `Bundled/`. The installer verifies that same SHA-256 before extracting BepInEx for a dedicated-server install.

The installer supports the same three roles as the fallback BAT file:

- Client
- Dedicated Server
- Host & Play

It preserves existing BepInEx installations, preserves existing server config/signing identity, refuses to overwrite an unknown `winhttp.dll`, and removes a legacy packed `version.dll` only when its SHA-256 matches the known historical AutoModSync bootstrap.

### Source/ValheimAutoModSync.Apply.cs

The apply helper runs outside Valheim after a verified download. Its job is intentionally narrow:

- wait for the old Valheim process to exit;
- move/replace only files listed in AutoModSync's pending staging file, mapping each signed kind to the fixed plugins/patchers/config root;
- preserve the reconnect token for the new client process;
- relaunch through the captured package-manager/Steam context when available.

This helper does not discover mods, fetch network content, decide server trust, or bypass validation.

### Source/AutoModSync.BuildTool.cs

Build/install utility functions include:

- SHA-256 calculation;
- dedicated-server path discovery;
- creation/preservation of the server RSA identity;
- a legacy packed-bootstrap writer retained for historical tooling compatibility.

Current transparent releases do not use the old packed `version.dll` bootstrap.

## Restart and reconnect flow

```text
verified bundle
  -> staging/{plugins|patchers|config}/*.amsnew
  -> pending.txt
  -> reconnect.txt
  -> optional launch-context.txt
  -> ValheimAutoModSync.Apply.exe
  -> old Valheim exits
  -> helper replaces files
  -> helper relaunches Valheim
  -> client consumes reconnect.txt
  -> FejdStartup reconnect
  -> 2.5.0 preflight runs again
  -> normal mod handshakes continue
```

## Fail-open / fail-closed behavior

AutoModSync does not make unrelated servers depend on AutoModSync. If an outgoing server does **not** answer the discovery probe, the client releases the held vanilla `ServerHandshake` after the short discovery timeout.

On `dev/2.6`, once the remote endpoint positively enters the AMS preflight path, AMS owns the outcome for that join. Invalid acknowledgements, manifest/signature/trust failures, unsafe paths, resource-limit violations, server-reported AMS errors, corrupt bundles, or apply/restart preparation failures abort that join and keep the original `ServerHandshake` from being replayed. They do not become a route around synchronization.

After a successful trusted/matching preflight, the original ServerHandshake is replayed unchanged. Jotunn, ServerSync-style mods, Epic Loot, and other compatibility systems still make their normal decisions afterward.

## Release signing

The branch contains:

- `SIGNING.md`
- `sign-release.ps1`
- `build-signed-release.bat`

The intended public-release path Authenticode-signs only AutoModSync-authored PE files, verifies their signatures before packaging, and fails a signing-required release if any signature is missing/invalid. Third-party BepInEx/Doorstop binaries are never re-signed as though they were authored by AutoModSync.

## Review-surface inventory

A source review of the five C# files shows the following intentional privileged surfaces:

- **No HTTP/WebClient/HttpClient downloader exists in the C# runtime.** Plugin bytes are transferred only over Valheim's existing `ZRpc` connection. The separate build/install scripts may obtain the pinned BepInEx package and verify its fixed SHA-256.
- **Process launch:** only the client starts `ValheimAutoModSync.Apply.exe`, and the helper starts Steam/Valheim for the requested restart. The installer itself does not launch downloaded code or fetch executables.
- **Registry access:** BuildTool and the apply helper read Steam install locations; they do not write registry values.
- **Native Windows imports:** the client imports only `MessageBox`, `GetConsoleWindow`, and `ShowWindow` for first-contact trust UI and console presentation.
- **Filesystem mutation:** the client/helper write AutoModSync state/staging files and synchronized files only beneath validated BepInEx plugin, patcher, or explicitly allowlisted config roots. Core/game-root/managed-assembly destinations are not manifest targets. The server writes its signing identity/cache files.
- **Cryptography:** server RSA signs manifests; client RSA verifies those signatures; SHA-256 identifies server keys, bundles, and synchronized files.

These comments/inventories are intended to make review easier, not to replace review. A reviewer should treat executable statements, path checks, and cryptographic checks as authoritative.

## Review guidance

For a security/code review, start with these functions:

- Client: `PreparePreflightGate`, `BeginPreflightProbe`, `ResumeNormalHandshake`, `RPC_ManifestEnd`, `ExtractBundleToStaging`, `EnsureServerTrusted`, `SafeUnder`.
- Server: `RPC_Hello`, `EnsureManifest`, `ResolveBundleRecord`, `RPC_GetBundle`.
- Apply helper: `Main`, `SafeUnder`, `TryLaunchSavedContext`.
- Build tool: `EnsureIdentity`, `PrintSha256`.

The comments above each function are documentation only; executable behavior remains the source of truth.
