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

Phase 4 adds optional `bundle-resume1` without changing protocol version 4. During a resume-capable transfer the server publishes the exact raw chunk size and the client stores one bounded partial ZIP plus versioned metadata under `BepInEx/AutoModSync/resume`. Metadata is tied to the trusted server fingerprint, a SHA-256 identity of the exact signed requested file set, the complete bundle SHA-256/size, chunk geometry, and file count. On reconnect the client rounds any crash-shortened tail down to a complete chunk boundary and offers the retained prefix SHA-256. The server accepts a nonzero start chunk only after independently hashing the same prefix of its current immutable cache artifact and matching every bundle-identity field. Any fingerprint/change-set/artifact/chunk/prefix mismatch restarts from byte zero; the final full bundle SHA-256 and normal ZIP/file verification still run before staging. Only one partial bundle slot is retained, bounding abandoned resume disk use to the existing client compressed-bundle ceiling, and stale state expires on the next offer after 24 hours.

Compatibility remains capability-negotiated within AMS4. A 2.6 client talking to a pre-resume AMS4 server never sees `bundle-resume1`, so it sends the original bundle request and reads the original bundle header. A pre-resume AMS4 client talking to a 2.6 server never advertises `bundle-resume1`, so the server neither reads a resume extension nor writes resume header fields. Development-only one-shot markers can emulate each side independently for live one-client testing; release builds contain neither emulator.

The deterministic resume-state harness is also wired to a Windows GitHub Actions validation workflow. It compiles and exercises the production resume/path-safety sources directly, including cross-server fingerprint isolation, without building or publishing a release.

Phase 5 adds optional `bundle-scheduler1` while keeping AMS protocol 4. A bundle request is parsed and validated first, but an excess client does not acquire/build a ZIP or receive enlarged Steam transport settings until a FIFO active-transfer slot opens. The default is four active transfers plus a bounded queue of 32 additional validated requests; runtime clamps the active limit to 1..32 and the waiting limit to 0..1024. Requests beyond that configured admission capacity fail closed for the current join instead of consuming unbounded server memory. New clients that negotiated the capability receive `AMS4_QueueStatus` with their 1-based waiting position and active-slot occupancy; older AMS4 clients simply remain connected and wait for `BundleBegin`, so the scheduler does not require a protocol-version break.

Active transfer payload is metered by one server-wide token bucket (`AggregateSendRateMaxBytesPerSec`, default 64 MiB/s) rather than by independent per-connection ceilings alone. Each scheduler turn reserves at most `SchedulerGrantBytes` (default 1 MiB) for one peer, then advances round-robin. The reservation may contain several existing <=384 KiB binary batch messages, so the scheduler changes pacing/admission rather than the AMS4 batch format. Unused bytes are refunded when a chunk boundary or transport backpressure prevents the full grant from being sent.

Steam's live pending + unacked reliable-byte counters are converted to an approximate queue-time envelope using the current reported send rate. At the default `SchedulerMaxSteamQueueMs=200`, AMS stops adding new bundle payload to that peer until the reliable queue drains instead of trying to fill the configured 32 MiB send buffer. This keeps the large buffer a ceiling rather than a target.

Each active peer now holds one open read-only sequential stream on the immutable cached ZIP and seeks only when the requested chunk offset differs from the current position. Completion/disconnect/idle cleanup closes that stream, restores any temporary Steam settings, releases the artifact reference, and frees the scheduler slot. A connected peer with an outstanding scheduled request is never expired merely because AMS itself is rate-limiting or backpressured.

The production scheduler policy is intentionally isolated in `AutoModSync.TransferScheduler.cs`. Its deterministic Windows CI harness emulates eight peers against four active slots and tests FIFO promotion, aggregate budget + burst, four-peer fairness, refunded blocked grants, queued removal, bounded admission, and idle-expiry semantics for outstanding demand. The first CI run caught a real fairness defect where an underfilled token bucket advanced the cursor and skipped a peer; the implementation now holds that peer's turn until enough tokens exist for its fixed grant.

For Steam-backed dedicated-server peers, the server resolves the transport directly from the active `ZRpc` (rather than depending on an early pre-handshake peer already appearing in `ZNet.GetPeers()`). During the bundle it temporarily tunes that exact Steam connection's send-rate maximum, bounded send-rate minimum, and reliable send buffer, then restores the exact previous values it changed. This keeps the acceleration scoped to synchronization instead of permanently rewriting gameplay networking.

The client hello also advertises `roots1`. That capability means the client/apply helper understand the fixed `P`/ `R`/ `C` destinations. A server only requires it when its signed manifest actually contains patcher or config entries, so ordinary plugin-only AMS4 compatibility remains available.

Phase 6 does not add a network capability or protocol version. After the manifest signature and trusted fingerprint have already been validated, the client loads only that fingerprint's ledger under `BepInEx/AutoModSync/ownership/<fingerprint>.txt`. A path becomes server-owned only when AutoModSync must actually install/replace it. If an unowned local file already has the exact signed bytes, it satisfies the manifest but remains local/user-owned.

When a later signed manifest omits an owned path, the client compares the live file against the ledger's exact last-installed size/SHA-256. Deletion requires two independent conservative conditions: the live bytes must still be exact, and `last-successful-server.txt` must identify this same trusted fingerprint as the immediately prior successful AMS reconciliation. A server switch therefore cannot trigger stale cleanup from another context; exact stale ownership is retained/deferred until a later consecutive sync with that server. Missing files simply lose ownership. Locally modified files/directories are preserved and ownership is relinquished instead of treating signed omission as broad delete authority. A different trusted server loads a different ledger and therefore cannot retire the first server's owned paths.

The desired post-commit ledger is written to `ownership-next.txt` before `pending.txt`; `pending.txt` remains the startup-recovery trigger. If only ownership metadata changes because a stale file was already missing or locally modified, the client can publish that ledger durably without restarting because no synchronized live file is being mutated. A successful zero-delta reconciliation durably records the current fingerprint as the immediately prior successful server; a sync that still needs an apply/restart does not gain that status until the post-restart manifest matches.

Phase 7 separates presentation from synchronization policy. `AutoModSync.SyncUiState.cs` is a deterministic state/telemetry model only: it records lifecycle phase, already-verified server fingerprint, manifest comparison counts, required expanded bytes, scheduler status, transfer bytes/rates/ETA, resume baseline, and verification progress. It never decides whether a server is trusted, whether a join fails open/closed, which files are requested/applied, when a scheduler slot is granted, or how fast bytes are sent. Those decisions remain in the existing client/server/apply paths.

The client renderer now consumes that model to draw one branded IMGUI panel with the tracked AMS logo, charcoal/slate surfaces, orange/ember accents, comparison stat tiles, queue occupancy, download progress/current+average throughput/ETA, explicit retained resume bytes, verification progress, server-identity context, and apply/restart/reconnect/failure states. The logo is embedded in the client DLL at build time as `ValheimAutoModSync.Branding.Logo.png`; a decode/resource failure logs a warning and falls back to text branding rather than affecting synchronization. The renderer uses only Unity's built-in IMGUI primitives plus the embedded texture, so no separate UI framework becomes a runtime dependency.

Normal player UI no longer prints the complete server fingerprint or a persistent fingerprint fragment. First contact shows a 64-bit human comparison code such as `0123-4567-89AB-CDEF`, derived from the public SHA-256 fingerprint solely for optional out-of-band comparison. The native trust dialog explains that the shortened code is display-only: signature validation, trust persistence, server-change detection, ownership scoping, and every equality decision still use the complete 256-bit fingerprint. Already-trusted sessions display only `TRUSTED SERVER`. Normal client/server logs likewise use the short comparison code; the full public fingerprint remains available only from explicit administrator identity tooling and the private client trust state that requires it.

Server-browser presence is intentionally separate from synchronization discovery. On a dedicated Steam backend the server can advertise `automodsync` and `automodsync_protocol` as ordinary Steam server rules after SteamGameServer reports a logged-on state. Those rules carry only product capability/version information and do not contain the server signing fingerprint, private identity, endpoint, or player identifiers. Before the client modifies Valheim's browser rows, the development-only `phase7-test-server-browser-probe.once` path captures the exact live `ServerListGui` row hierarchy and bounded field metadata so the final logo badge can be attached to a stable existing row anchor instead of hard-coded screen coordinates.

Development builds can consume `BepInEx/AutoModSync/phase7-test-ui-preview.once` and cycle ten deterministic presentation snapshots at the main menu. That path exists only under `AMS_DEV_TESTS`, performs no network/filesystem synchronization action beyond consuming its marker, and is cancelled as soon as a real preflight connection begins. This lets visual layout be qualified independently before a real large-payload run.

The canonical tracked package logo (`Thunderstore/icon.png`) also feeds `build-branding-assets.ps1`, which produces a multi-size Windows ICO. Development/release builds embed that icon into the Apply helper; release builds also embed it into the standalone installer, while packaging/deployment keeps the physical `.ico` beside the corresponding executable. This makes executable branding deterministic instead of relying on a Visual Studio-only project setting.

`verify-no-pii.ps1` is a first-party privacy gate used by both development and release builders. It rejects literal local user-profile/home paths, email addresses, Windows account SIDs, SteamID64-like identifiers, and public IPv4 literals from tracked first-party text. After compilation it also scans printable strings from AutoModSync-authored PE artifacts. Third-party license attribution is excluded so required notices remain verbatim; the public GordonFreesay/AutoModSync project brand, repository URLs, and website URL are intentionally treated as product identity rather than private user data.

### Source/ValheimAutoModSync.Server.cs

Phase 6 also introduces a fixed **client-only plugin payload** at `BepInEx/AutoModSync/ClientPayload/plugins/**`. Because that directory is outside `BepInEx/plugins`, BepInEx does not load those files into the dedicated server. The server recursively scans the tree without following reparse points, applies the existing `ExcludePatterns`/hard exclusions, hashes each file, and emits it as the existing signed `P:<relative>` destination. The explicit client-only tree is inherently client-required, so `ServerOnlyPatterns` and `ClientRequiredPatterns` do not reclassify it. Nested DLL dependencies/assets are preserved by relative path.

Normal plugins and ClientPayload share the same final client namespace, so final manifest assembly uses a case-insensitive destination registry. If two sources would produce the same `P:<relative>` destination, manifest construction fails explicitly; there is no last-source-wins behavior. This keeps client-only packaging auditable without adding a new destination kind or weakening the existing signed-manifest/bundle/apply path.

The server plugin registers AMS4 RPCs on incoming Valheim connections and serves a deterministic signed view of eligible files from fixed BepInEx roots: plugins (`P`), patchers (`R`), and explicitly allowlisted config (`C`). Server-only/client-required pattern rules are applied before the canonical manifest is signed.

2.5.0 adds an optional `AMS4_Ack` immediately after a valid hello and before manifest hashing. New clients use that acknowledgement to distinguish a slow manifest build from a non-AutoModSync server. Older clients ignore the unknown acknowledgement and continue to understand the existing AMS4 manifest messages.

The server never opens a second listener or contacts an external download service.

On `dev/2.6`, bundle construction is content-addressed. The server sorts the exact requested signed records, hashes kind/path/size/content-hash metadata into a bundle cache key, builds a ZIP privately, hashes the completed ZIP, and only then publishes it as an immutable artifact. Identical clients reuse that artifact for `BundleCacheSeconds` instead of recompressing the same files. If identical requests arrive while the artifact is still being built, they join one single-flight build and consume the same published ZIP afterward. Active transfers reference-count the artifact so cache cleanup cannot delete a ZIP another client is reading; idle artifacts are TTL/LRU-evicted under `BundleCacheMaxMiB`. Cache metadata is process-local and startup deletes orphaned ZIP/temp files rather than trusting artifacts from a previous process.

Dedicated servers additionally prewarm the dominant public-server case during initial plugin startup when `PrebuildFreshClientBundle=true`: every signed distributable record except `ValheimAutoModSync.Client.dll`, because a client must already have the AutoModSync client plugin in order to request synchronization. That startup artifact is pinned against TTL expiry until its first real client hit (the disk-budget policy may still evict it), so an otherwise idle public server does not lose the prewarm merely because no player joins within the ordinary cache TTL. After the first live hit, normal TTL/LRU behavior resumes. Clients with a different partial/delta set still select their own exact content key and build lazily on the first such request.

Bundle-build telemetry distinguishes `MISS`, `HIT`, and `WAIT-HIT` and records ZIP construction, final ZIP SHA-256, total preparation, and any single-flight wait time. While copying source bytes into a new artifact, the server simultaneously re-hashes each source against the signed manifest record so a same-size local change cannot be published under an obsolete cache key.

For `bundle-resume1`, the server does not trust a client-supplied offset by itself. It first resolves/builds the same signed immutable bundle as a normal request, compares the offered SHA/size/chunk geometry/file count, hashes the exact claimed prefix from the server artifact, and returns a nonzero start chunk only on an exact prefix match. A server restart or cache rebuild that produces different ZIP bytes therefore rejects the old partial safely. The server also polls active transfer RPCs and releases dead-peer artifact references plus any temporary Steam transport tuning after disconnect, so an interrupted client does not pin cache state indefinitely.

For Phase 5, `RPC_GetBundle` no longer immediately owns a transfer slot. Validated requests enter the FIFO scheduler first. Admission happens on the server update loop, with at most one potentially expensive artifact acquisition/build started per frame. Chunk/window RPCs register exact raw-byte demand instead of writing the whole requested pipeline synchronously; the update loop services one bounded grant per active peer in round-robin order under the aggregate token bucket and Steam queue envelope. This prevents a single deep-pipeline client from monopolizing application-level writes and bounds active per-client stream/buffer state independently of payload size.

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
- recover any prior interrupted apply transaction before starting another one;
- map only pending signed kinds to the fixed plugins/patchers/config roots;
- verify every staged source and create durable old-state backups before the first live write;
- write a durable `PREPARED` marker, apply/verify every destination while retaining staging, then write `COMMITTED`;
- on PREPARED interruption, restore the complete old set before retrying; on COMMITTED interruption, keep the complete new set and finish cleanup;
- preserve the reconnect token for the successfully applied/relaunched client process;
- relaunch through the captured package-manager/Steam context when available.

The transaction lives under `BepInEx/AutoModSync/apply-transaction`. New Phase 6 transactions use `AMSTXN2`, which records whether each fixed-root operation is a verified write or stale-owned delete plus old/new rollback metadata. Recovery still accepts older `AMSTXN1` write-only journals so upgrading the helper cannot strand a pre-existing interrupted Phase 2 transaction. `pending.txt` and verified `.amsnew` staging remain present until COMMITTED cleanup, so a pre-commit crash has enough information to roll back and retry rather than accepting a partially updated install. The helper records transaction milestones in `apply.log`.

Before PREPARED, the helper independently validates `ownership-next.txt` against the currently trusted fingerprint-scoped ledger and the prepared operations. New/changed ownership requires a matching verified write. A delete requires exact same-server prior ownership and the live bytes must still match that last-owned digest after Valheim exits. A PREPARED deletion is backed up and restored on rollback exactly like a replaced file. The transaction copy of the desired ownership ledger is published only after COMMITTED; if the helper dies after COMMITTED, recovery preserves the complete new live state and idempotently publishes the ledger before cleanup.

The client no longer performs leftover staging copies from inside a running Valheim process. If startup sees `pending.txt` or `apply-transaction`, it starts the external helper and exits/restarts before attempting any AMS server join.

For maintainer validation, `build-dev.bat` alone defines `AMS_DEV_TESTS` for the Apply helper. That development binary recognizes one-shot local pause markers after PREPARED/before the first live write, after a chosen number of applied files, after rollback, or after COMMITTED, plus a caught-failure marker after a chosen number of applied files. This allows deterministic process termination, rollback inspection, and synchronous error-path testing at exact transaction boundaries. `test-phase2-adversarial.ps1` drives the remaining journal/path-safety cases against an isolated temporary BepInEx tree. The public/release build path does not define this symbol, so none of these fault-injection hooks are part of release binaries.

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
  -> durable pending.txt
  -> reconnect.txt
  -> optional launch-context.txt
  -> ValheimAutoModSync.Apply.exe
  -> old Valheim exits
  -> recover prior journal if present
  -> snapshot old destinations + transaction manifest
  -> durable PREPARED
  -> apply + verify every live destination (staging retained)
  -> durable COMMITTED
  -> remove staging/pending/transaction backups
  -> helper relaunches Valheim
  -> client consumes reconnect.txt
  -> FejdStartup reconnect
  -> 2.6 preflight runs again
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
