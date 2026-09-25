# AutoModSync 2.6 implementation ledger

This document is the repository-authoritative change ledger for AutoModSync 2.6 development.

## Development baseline

- Integration branch: `dev/2.6`
- Baseline main commit: `cf73fd97e1a13ac293783d722e112b14d83b2d17`
- Phase 0 baseline commit: `ac49516c0b2f3898c5fff2ed820e46c2c6d93888`
- Public release while development is in progress: **2.5.0**
- Development software version: **2.6.0**
- Network protocol baseline: **AMS4 / protocol 4**
- No 2.6 public release/tag is authorized until the validation gates in this document are completed.

## Engineering rules

1. Preserve the proven 2.5 preflight/ServerHandshake hold-and-replay model unless a reviewed 2.6 change explicitly replaces part of it.
2. New security-sensitive or non-obvious code must include concise intent/workflow/safety comments so source can be audited without reconstructing design intent from history.
3. Every file changed specifically for 2.6 must be listed in the phase ledger below with the reason it changed.
4. Keep changes phase-scoped and reviewable; do not combine unrelated rewrites.
5. Do not weaken server identity verification, signed-manifest verification, fixed destination roots, config opt-in ownership, or third-party compatibility validation.
6. Do not introduce arbitrary remote command execution, arbitrary filesystem destinations, encoded PowerShell, packed/obfuscated bootstrap behavior, or runtime installer downloads.
7. Build/test results are evidence only when an actual run exists. Do not record assumed success.
8. Release builds/publication are deferred until the maintainer has functionally validated the 2.6 implementation.

## Approved implementation phases

| Phase | Scope | Gate |
| --- | --- | --- |
| 0 | Development branch/version/documentation baseline | Repository metadata identifies 2.6 development without changing the public 2.5 release |
| 1 | Security/resource foundation | Invalid signature/path/size/reparse inputs make zero live-file changes; non-AMS joins still fail open |
| 2 | Transactional apply/recovery | Interrupted apply recovers to complete-old or complete-new state, never an accepted mixed state |
| 3 | Cached/single-flight bundle construction | Identical clients share one immutable built artifact |
| 4 | Resume | Exact-artifact partial transfer safely resumes after disconnect |
| 5 | Concurrent-transfer scheduler | Bounded/fair aggregate transfer behavior under 1/2/4/8 clients |
| 6 | Content ownership/client payload | Client-only payload and conservative same-server stale ownership rules |
| 7 | Telemetry/in-game product UI | Trust/sync/verify/restart/reconnect/failure states are clearly represented |
| 8 | Installer productization | Upgrade/repair/uninstall preserve unrelated files and server identity by default |
| 9 | Qualification/release | Large-payload public-demo matrix and backward compatibility pass before release packaging |

## File change ledger

### Phase 0 — development baseline

| File | 2.6 reason |
| --- | --- |
| `VERSION` | Marks future development artifacts as 2.6.0 so they cannot be mistaken for public 2.5.0 binaries. |
| `Source/ValheimAutoModSync.Client.cs` | Development assembly/plugin version metadata only; protocol remains 4. |
| `Source/ValheimAutoModSync.Server.cs` | Development assembly/plugin version metadata only; protocol remains 4. |
| `Source/ValheimAutoModSync.Apply.cs` | Development assembly version metadata only. |
| `Source/ValheimAutoModSync.Installer.cs` | Development assembly/product version metadata only. |
| `Source/AutoModSync.BuildTool.cs` | Development assembly version metadata only. |
| `Source/AutoModSyncInstaller.manifest` | Keeps installer assembly identity consistent with development version. |
| `Thunderstore/thunderstore.toml` | Keeps repository version-consistency checks aligned; this does **not** authorize publication. |
| `Thunderstore/CHANGELOG.md` | Adds an explicit 2.6 development section while preserving 2.5 release history. |
| `IMPLEMENTATION-2.6.md` | New authoritative roadmap/change ledger for 2.6 development. |

### Phase 1 — security/resource foundation

| File | 2.6 reason |
| --- | --- |
| `Source/AutoModSync.PathSafety.cs` | New shared, commented Windows path validator for reserved names, aliases, fixed-root containment, and reparse-point defense. |
| `build-release.bat` | Compiles the shared path-safety source into client, server, and apply-helper assemblies. No release build has been run. |
| `Source/ValheimAutoModSync.Client.cs` | Separates discovery fail-open from recognized-AMS fail-closed; establishes trust after signed-manifest verification; adds client hard limits, declared-size enforcement, bounded ZIP extraction, shared path/reparse checks, and one-shot restart-reconnect guards that suppress duplicate character-selection callbacks after completion or while a start is already pending. |
| `Source/ValheimAutoModSync.Server.cs` | Adds safe non-reparse recursive enumeration, fixed-root source rechecks, pre-compression expanded-size ceiling, during-build compressed-size enforcement, temporary Steam transfer telemetry/tuning, and the evidence-driven 16/64/32 development transfer baseline with exact-old-default migration. |
| `Source/ValheimAutoModSync.Apply.cs` | Independently rechecks shared path/reparse rules immediately before staged/live filesystem writes. |
| `Server/server-config-example.cfg` | Documents `MaxExpandedBundleMiB`, aligns exclusion defaults, and records the current 16 MiB/s minimum / 64 MiB/s maximum / 32 MiB transfer-buffer development baseline. |
| `SOURCE-WALKTHROUGH.md` | Documents current 2.6 trust, fail-open/fail-closed, resource-limit, and filesystem-containment behavior. |
| `Thunderstore/CHANGELOG.md` | Records 2.6 Phase 1 development behavior; publication remains deferred. |
| `TESTING-2.6.md` | Evidence checklist plus dated maintainer runtime results; only observed cases are marked passed and the remaining security/resource matrix stays pending. |
| `build-dev.bat` | Development-only compiler for client/server/apply binaries; deliberately creates no installer, release ZIP, store package, tag, or publication artifact. |
| `IMPLEMENTATION-2.6.md` | Records this Phase 1 change set and validation status. |

### Phase 2 — transactional apply/recovery

| File | 2.6 reason |
| --- | --- |
| `Source/ValheimAutoModSync.Apply.cs` | Replaces per-file destructive apply with a durable PREPARED/COMMITTED transaction journal. Before any live write it verifies staging, snapshots every existing destination, records originally-absent destinations, and flushes rollback metadata. New files are copied from staging without consuming staging, promoted through same-directory temporary files, and verified before COMMIT. Interrupted PREPARED transactions roll back the complete old set; interrupted COMMITTED transactions preserve the complete new set and finish cleanup. Under `AMS_DEV_TESTS` only, one-shot pause/failure markers provide deterministic boundaries for crash and caught-error validation. |
| `Source/ValheimAutoModSync.Client.cs` | Publishes `pending.txt` through a write-through temporary file + same-volume rename and no longer copies leftover staging into live roots from inside a running Valheim process. Pending/journaled state is handed back to the helper and forces a recovery restart before AMS can join a server. |
| `SOURCE-WALKTHROUGH.md` | Documents the transaction journal, PREPARED/COMMITTED recovery boundary, backup/staging lifetime, and restart behavior. |
| `TESTING-2.6.md` | Adds normal, pre-commit interruption, post-commit cleanup interruption, rollback, journal-validation, and path-safety tests for Phase 2. |
| `Thunderstore/CHANGELOG.md` | Records the 2.6 transactional apply/recovery behavior in the development changelog. |
| `build-dev.bat` | Defines `AMS_DEV_TESTS` only for the development Apply helper, enabling one-shot pre-COMMIT, post-rollback inspection, post-COMMIT, pre-first-write, and caught-failure test hooks used by the Phase 2 gate. Release builds do not define the symbol and therefore do not contain these fault-injection paths. |
| `test-phase2-adversarial.ps1` | Runs the remaining Phase 2 helper adversarial cases in an isolated temporary BepInEx tree: caught per-file failure rollback, journal version rejection, protected/fixed-root path rejection, and reparse-point rejection. It does not touch the real Valheim install or server. |
| `IMPLEMENTATION-2.6.md` | Records the Phase 2 source/docs change set and keeps implementation status separate from runtime validation. |

### Phase 3 — cached/single-flight bundle construction

| File | 2.6 reason |
| --- | --- |
| `Source/ValheimAutoModSync.Server.cs` | Adds deterministic content-keyed immutable bundle artifacts, identical-request single-flight construction, active-transfer reference counting, TTL/LRU disk-budget eviction, orphan cleanup, per-source re-hash before publication, detailed MISS/HIT/WAIT preparation telemetry, and dedicated-server startup prewarming of the current-AMS nearly-bare-client baseline. The startup baseline stays TTL-pinned until its first real client hit, while remaining subject to the configured disk budget. Protocol remains AMS4 / 4. |
| `Source/ValheimAutoModSync.Client.cs` | Runs the native Win32 first-contact fingerprint Yes/No dialog on a background STA thread so Unity/ZRpc networking continues while the user verifies identity, without AutoModSync changing Unity cursor visibility/lock state. Binds acceptance to the same live signature-verified RPC/manifest, invalidates stale dialog results by generation, best-effort closes the native prompt if the protected connection ends first, clears transient AMS state fail-closed, and makes blocked-join status self-expire after about four seconds. |
| `Server/server-config-example.cfg` | Documents `BundleCacheSeconds`, `BundleCacheMaxMiB`, and `PrebuildFreshClientBundle`, including first-client pinning and active-transfer-safe eviction semantics. |
| `SOURCE-WALKTHROUGH.md` | Documents the content-addressed cache lifecycle, single-flight behavior, publication boundary, source re-hash, and telemetry. |
| `TESTING-2.6.md` | Adds the Phase 3 runtime gate for cache hit/miss, overlapping identical clients, invalidation, TTL/budget eviction, failed-build cleanup, and restart orphan handling. |
| `IMPLEMENTATION-2.6.md` | Records this Phase 3 implementation and keeps its validation status distinct from implementation status. |

### Phase 4 — exact-artifact resumable transfer

| File | 2.6 reason |
| --- | --- |
| `Source/AutoModSync.ResumeState.cs` | Adds shared versioned resume-state and exact-prefix verification. The client keeps one bounded partial ZIP slot tied to trusted server fingerprint, exact signed request key, bundle SHA/size, chunk geometry, and file count; the server independently hashes the claimed retained prefix before accepting any nonzero offset. |
| `Source/ValheimAutoModSync.Client.cs` | Advertises optional `bundle-resume1`, persists only complete-chunk bundle prefixes across recognized connection loss, offers prefix metadata on reconnect, reopens only a server-verified exact prefix, and otherwise restarts from zero. Development builds add one-shot deterministic socket-close and pre-resume-client emulation markers for single-client runtime validation. |
| `Source/ValheimAutoModSync.Server.cs` | Advertises `bundle-resume1`, parses resume offers only for clients that negotiated it, verifies exact artifact identity plus prefix SHA-256 before returning a nonzero start chunk, and releases cache/Steam transport state for dead mid-transfer peers. Development builds add a one-shot pre-resume-server emulation marker that withholds the capability and keeps the original AMS4 request/header shape. |
| `build-dev.bat` | Compiles the shared resume source into client/server and defines `AMS_DEV_TESTS` for both roles so transfer interruption and legacy-peer compatibility emulators are development-only. |
| `build-release.bat` | Compiles the shared resume source into production client/server without any development fault-injection symbol. No release build has been authorized or run. |
| `test-phase4-resume.ps1` | Compiles the production resume/path-safety sources into an isolated temporary harness and emulates interrupted, truncated, corrupt, wrong-change-set, wrong-server-fingerprint, changed-artifact/chunk-geometry, expired, malformed, and complete bundle-prefix cases without touching the real Valheim installation. |
| `.github/workflows/phase4-resume-validation.yml` | Runs the deterministic Phase 4 resume harness on a Windows GitHub Actions runner when the resume implementation, path-safety dependency, harness, or workflow changes; this provides an independent rerun of the exact production helper code without producing a release artifact. |
| `SOURCE-WALKTHROUGH.md` | Documents the optional AMS4 resume capability, exact-prefix trust boundary, bounded client state, and safe fallback-to-zero behavior. |
| `TESTING-2.6.md` | Adds deterministic helper and one-client real-socket interruption gates for Phase 4. |
| `Thunderstore/CHANGELOG.md` | Records Phase 4 development behavior without publishing a release. |
| `IMPLEMENTATION-2.6.md` | Records the Phase 4 source/docs change set and keeps implementation separate from observed validation. |

### Phase 5 — concurrent transfer scheduler / aggregate server budget

| File | 2.6 reason |
| --- | --- |
| `Source/AutoModSync.TransferScheduler.cs` | Adds the pure FIFO admission + round-robin aggregate token-bucket policy used by the server. It limits active peers, preserves waiting order, meters raw payload bytes under one server-wide budget, and supports grant refunds when runtime backpressure prevents a send. |
| `Source/ValheimAutoModSync.Server.cs` | Queues bundle requests before artifact acquisition, admits at most the configured active count, reuses one open sequential ZIP stream per active peer, schedules existing AMS4 chunk/batch responses in fair bounded grants, applies Steam reliable-queue backpressure, reclaims dead/idle slots, and sends optional queue position to capable clients. |
| `Source/ValheimAutoModSync.Client.cs` | Advertises optional `bundle-scheduler1`, registers `AMS4_QueueStatus`, and shows a simple queued-for-synchronization state while preserving silent compatibility with older AMS4 peers. |
| `Server/server-config-example.cfg` | Documents active-slot, aggregate-bandwidth, scheduler-grant, Steam-queue-envelope, and idle-timeout controls. |
| `build-dev.bat` | Compiles the production scheduler source into the development server runtime. |
| `build-release.bat` | Compiles the same production scheduler source into the release server path without adding any new release-only behavior. No release build has been authorized or run. |
| `test-phase5-scheduler.ps1` | Compiles the production scheduler source into an isolated deterministic 8-peer harness covering admission, FIFO promotion, aggregate budget, fairness, refund/backpressure semantics, and queued removal. |
| `.github/workflows/phase5-scheduler-validation.yml` | Runs the deterministic scheduler harness on Windows when the scheduler/harness/workflow changes. It produces no release artifact. |
| `SOURCE-WALKTHROUGH.md` | Documents Phase 5 admission, token-bucket fairness, persistent per-transfer streams, queue backpressure, and AMS4 compatibility. |
| `TESTING-2.6.md` | Adds the deterministic and live Phase 5 validation gates and records the CI-discovered fairness fix. |
| `Thunderstore/CHANGELOG.md` | Records Phase 5 development behavior without publishing a release. |
| `IMPLEMENTATION-2.6.md` | Records the complete Phase 5 source/docs change set and separates deterministic policy validation from live socket qualification. |

### Phase 6 — trusted-server content ownership / stale payload lifecycle

| File | 2.6 reason |
| --- | --- |
| `Source/AutoModSync.OwnershipState.cs` | Adds strict fingerprint-scoped ownership ledgers for files AutoModSync actually installed plus a durable immediately-prior-successful-server marker used to gate stale deletion after server switches. Matching pre-existing local files are not claimed. Ledger entries carry fixed kind/path/size/SHA-256 and reject protected config names. |
| `Source/AutoModSync.ClientPayload.cs` | Adds shared/testable recursive scanning for the fixed server `BepInEx/AutoModSync/ClientPayload/plugins` tree, exact SHA-256 metadata, exclusion filtering, reparse/path containment, and case-insensitive final-destination collision registration. |
| `Source/ValheimAutoModSync.Client.cs` | Builds desired ownership only from a verified trusted manifest, claims unowned paths only when AMS must install/replace them, schedules exact-digest stale deletions only after a consecutive successful reconciliation with the same trusted fingerprint, preserves modified stale/local files while relinquishing ownership, supports delete-only apply/restart, records the immediately prior successful server on zero-delta reconciliation, and publishes metadata-only relinquishment without live-file mutation. No AMS wire protocol change is introduced. |
| `Source/ValheimAutoModSync.Apply.cs` | Extends the durable transaction to operation-aware `AMSTXN2` write/delete entries while retaining `AMSTXN1` recovery compatibility. Same-server ownership authority and exact stale digest are independently revalidated before PREPARED; delete rollback and COMMITTED ownership publication share the existing complete-old/complete-new crash boundary. |
| `Source/ValheimAutoModSync.Server.cs` | Adds the fixed client-only plugin source root under `BepInEx/AutoModSync/ClientPayload/plugins`, maps it to ordinary signed `P` destinations, keeps it outside the server's loadable plugin tree, treats every non-excluded payload file as client-required, and rejects final destination collisions instead of allowing source shadowing. |
| `build-dev.bat` | Compiles ownership state into the development client/Apply helper and the client-payload scanner into the development server. |
| `build-release.bat` | Compiles the same ownership/client-payload sources into the release-path client/helper/server without enabling development fault hooks. No release artifact has been authorized or built. |
| `test-phase6-ownership.ps1` | Compiles production Apply/ownership/path-safety sources and exercises ownership acquisition, exact deletion, wrong-digest/cross-server rejection, PREPARED rollback, and COMMITTED recovery entirely inside a temporary BepInEx tree. |
| `test-phase6-clientpayload.ps1` | Compiles production client-payload/ownership/path-safety sources and exercises recursive payload discovery/hash metadata, exclusion behavior, collision rejection, and immediately-prior-server marker semantics in an isolated temporary tree. |
| `test-phase6-live.ps1` | Provides a bounded guided three-join live lifecycle fixture under a dedicated harmless test subtree: initial AMS acquisition versus matching pre-existing local bytes, rename + modified-local preservation/relinquishment, delete-only cleanup, and ownership/apply-log inspection. It never edits trust/config/real mod DLLs or ownership metadata directly. |
| `.github/workflows/phase6-ownership-validation.yml` | Runs the isolated ownership/apply harness on Windows when Phase 6 transaction/ownership sources or the harness change. |
| `.github/workflows/phase6-clientpayload-validation.yml` | Runs the isolated client-payload/prior-server production-policy harness on Windows. |
| `.github/workflows/apply-transaction-regression.yml` | Recompiles the current development Apply helper and reruns the closed Phase 2 adversarial transaction suite whenever ownership/journal/apply safety sources change. |
| `test-phase2-adversarial.ps1` | Updates Phase 2 journal-manipulation expectations for newly-written `AMSTXN2` while preserving the underlying rollback/path-safety regression coverage. |
| `SOURCE-WALKTHROUGH.md` | Documents fingerprint-scoped ownership, conservative stale deletion authority, metadata-only relinquishment, and AMSTXN1-to-AMSTXN2 recovery compatibility. |
| `TESTING-2.6.md` | Adds deterministic and live Phase 6 lifecycle gates and records observed CI evidence only. |
| `Thunderstore/CHANGELOG.md` | Records Phase 6 development behavior without publishing a release. |
| `IMPLEMENTATION-2.6.md` | Records the Phase 6 implementation/trust-boundary change set and validation status. |

### Installer polish / uninstall

| File | Purpose |
| --- | --- |
| `Source/ValheimAutoModSync.Installer.cs` | Polishes the standalone Windows installer with embedded AMS branding, live complete/partial install detection, `Repair / Update`, and a role-aware `Uninstall` button that appears only for complete selected-role installs. Client uninstall uses strict ownership metadata + live SHA-256 before retiring synchronized files, blocks on pending apply recovery, preserves changed/local files and shared BepInEx; server uninstall preserves operator ClientPayload and signing identity/config by default, with explicit opt-in identity removal. |
| `test-installer-contract.ps1` / `.github/workflows/installer-validation.yml` | Compile the production installer with the shared identity/path/ownership helpers, enforce the branded/uninstall contract, run the source documentation gate, and PII-scan the compiled installer artifact. |

## Release provenance

| File | Purpose |
| --- | --- |
| `write-release-checksums.ps1` | Generates sorted shasum-compatible SHA-256 manifests for the exact standalone/store package bytes. A standalone local build writes a one-package manifest; the unified distribution build overwrites it with all four official package digests. |
| `.github/workflows/release-build.yml` | Attests the canonical standalone ZIP and checksum manifest with GitHub Artifact Attestations using the GitHub Actions OIDC identity. |
| `.github/workflows/distribution-packages.yml` | On manual/tag builds, validates the four-package checksum manifest, attests all four subject digests plus the manifest, and uploads the checksum file with the package artifacts. |
| `.github/workflows/publish-*.yml` | Each store workflow hashes and attests the exact store ZIP produced by that publishing run before uploading it, avoiding any assumption that separately rebuilt ZIPs are byte-identical. |
| `VERIFYING-RELEASES.md` | Gives users direct `gh attestation verify` and SHA-256 verification commands while explicitly distinguishing provenance from Authenticode/SmartScreen trust. |
| `test-release-provenance.ps1` / `.github/workflows/release-provenance-validation.yml` | Deterministically validate checksum ordering/digests, workflow permission/subject wiring, exact store coverage, and local-build non-attestation wording. |

## Phase 7 — telemetry / branded in-game product UI

| File | 2.6 reason |
| --- | --- |
| `Source/AutoModSync.SyncUiState.cs` | Adds a policy-free presentation model for lifecycle phase, server fingerprint, signed-manifest comparison counts, required expanded bytes, scheduler position, resume-aware transfer progress/current+average throughput/ETA, and per-file verification progress. It owns no networking, trust, filesystem, pacing, or admission decisions. |
| `Source/AutoModSync.IdentityDisplay.cs` | Centralizes display-only server identity formatting. The player-facing verification code contains 64 bits of the public fingerprint for human comparison, while all trust/signature/pinning logic continues to use the complete 256-bit fingerprint. Full formatting is reserved for explicit administrator tooling. |
| `Source/ValheimAutoModSync.Server.cs` browser presence | Adds opt-out `Discovery.AdvertiseAutoModSync` and publishes only `automodsync=<version>` plus `automodsync_protocol=<protocol>` through Steam server rules after the dedicated Steam game server is logged on. Publication is retry-only/best-effort and does not affect synchronization, ports, server names, fingerprints, or player data. |
| `Source/ValheimAutoModSync.Client.cs` browser probe | Adds a development-only one-shot probe that waits for Valheim's live `JoinPanel/ServerListGui`, then records bounded row hierarchy/component names and field type/count summaries. This is deliberately diagnostic rather than a guessed badge injection so the production badge can anchor to Valheim 1.0's real row structure. |
| `Source/ValheimAutoModSync.Client.cs` browser badge | Uses the confirmed `ServerElement/name` row structure and `m_filteredList` index mapping to query only visible dedicated rows for the public Steam `automodsync` rule. Queries are capped at four outstanding requests, positive/negative results are cached, abandoned queries time out, and the existing embedded AMS logo is inserted as a non-interactive 18px child of the server-name field. The advertised server name is never rewritten. `Discovery.ShowServerBadges` can disable the presentation. |
| `Source/ValheimAutoModSync.Client.cs` | Maps the existing trusted synchronization decisions into `AutoModSyncUiState` and renders a gray/charcoal/orange branded IMGUI panel for trust, compare, queue, download, verification, apply/restart, reconnect, completion, and failure. The client embeds the canonical AMS logo and degrades to text branding if that resource cannot be decoded. Development builds also contain a one-shot presentation-only preview that cycles all major states without opening a connection or changing files. |
| `build-branding-assets.ps1` | Generates a multi-size PNG-backed Windows ICO from the canonical tracked AMS package logo so executable branding has one reproducible source asset. |
| `build-dev.bat` | Embeds the AMS PNG resource into the development client, references Unity ImageConversion for runtime PNG decoding, generates the helper ICO, embeds it into `ValheimAutoModSync.Apply.exe`, and retains the standalone ICO beside the development helper. |
| `build-release.bat` | Applies the same embedded client-logo and helper-icon path to release compilation, embeds the icon into the standalone installer, and packages physical ICO files beside the user-facing executables. Release builds still omit all `AMS_DEV_TESTS` preview hooks. |
| `deploy-dev.ps1` | Deploys and SHA-256 verifies the generated helper ICO beside the exact development Apply helper location in addition to the runtime binaries. |
| `build-thunderstore.ps1` / `build-modsite-package.ps1` / `install.bat` | Keep `ValheimAutoModSync.Apply.ico` beside `ValheimAutoModSync.Apply.exe` for package-manager, Nexus/CurseForge, and standalone installation paths. |
| `test-phase7-ui-state.ps1` | Deterministically validates the production presentation model: reset, comparison counters, transfer rate/ETA/progress, resume accounting, queue/verification bounds, and cross-session reset. |
| `test-phase7-identity-display.ps1` | Compiles the production identity-display helper and verifies deterministic 64-bit code formatting, invalid-input behavior, full technical formatting, and absence of legacy full/partial fingerprint presentation from normal client UI. |
| `test-phase7-server-browser-presence.ps1` | Arms the one-shot live browser-structure probe and verifies the dedicated server logged successful publication of the passive Steam AMS rule marker before badge rendering is implemented. |
| `verify-no-pii.ps1` / `.github/workflows/privacy-validation.yml` | Add a project-wide first-party privacy gate for tracked text plus AutoModSync-authored binaries, and independently validate the C# intent-comment requirement and identity display on Windows CI. Both dev and release builders invoke the guard before compilation and again against authored PE outputs. |
| `test-phase7-branding.ps1` | Generates and structurally validates the ICO, checks the production client state/branding wiring, and verifies dev/release/deploy/distribution packaging contracts. |
| `test-phase7-ui-preview.ps1` | Arms a one-shot development marker so the maintainer can visually inspect ten branded presentation states at the Valheim main menu without mutating synchronization state. |
| `.github/workflows/phase7-ui-state-validation.yml` / `.github/workflows/phase7-branding-validation.yml` | Independently rerun the deterministic model and branding/package contracts on Windows. |
| `SOURCE-WALKTHROUGH.md` / `TESTING-2.6.md` / `Thunderstore/CHANGELOG.md` | Document the presentation-only trust boundary, visual/runtime gates, and 2.6 development behavior. |

### Later phases

Add each 2.6-modified file here in the same phase that introduces the change, with a concise audit reason.

## Validation status

- Phase 0 repository setup: **complete**
- Phase 1 implementation: **complete in source; partial functional validation recorded**
- Phase 2 implementation and validation: **complete** — normal transactional apply, PREPARED interruption rollback/retry for originally-absent and pre-existing destinations, COMMITTED cleanup recovery, caught per-file failure rollback, malformed/version-mismatched journal rejection, fixed-root/protected-config rejection, and recovery reparse-point rejection have all passed observed validation. The isolated adversarial harness reported all four remaining error-path groups PASS without touching the real Valheim installation or server.
- Phase 4 implementation and validation: **complete** — the deterministic resume harness passes all 7/7 cases, including wrong-change-set and distinct trusted-server-fingerprint isolation; a one-client live run forced a real mid-transfer disconnect, restored server Steam transport state and released the dead transfer, resumed the same 313.4 MB cached artifact at chunk 4096/13374 with 96.0 MB retained after a 1.340 s server prefix verification, completed synchronization, applied transactionally, restarted, auto-reconnected, and matched the trusted server; and separate live one-file delta tests passed in both pre-resume AMS4 compatibility directions. GitHub Actions independently reran the updated harness on Windows Server 2025 and passed all 7/7 cases. Phase 4 is closed.
- Phase 5 implementation and validation: **complete** — bundle requests are FIFO-admitted before artifact acquisition, at most four transfers are active by default, at most 32 additional validated requests are retained in the bounded waiting queue by default, active peers share a 64 MiB/s aggregate raw-payload token bucket in round-robin grants, Steam reliable-queue pressure can refund/defer grants, each active peer retains one open sequential artifact stream, excess admitted clients receive optional queue status, queue-overflow joins fail closed instead of growing server memory without bound, and dead/idle slots are reclaimed. The production scheduler class passes 8/8 Windows CI checks after CI exposed and drove a refill-boundary fairness fix. Live validation passed the normal one-client scheduler/apply/reconnect path, a 313.4 MB transfer under a 4 MiB/s aggregate cap at 3.76 MiB/s average with repeated Steam backpressure deferrals and successful completion, and a pre-scheduler AMS4 compatibility transfer with no queue-status UI. The server configuration was restored to the normal 64 MiB/s aggregate cap afterward. True 2/4/8 independent-socket throughput is explicitly deferred to Phase 9 qualification and is not claimed as measured here. Phase 5 is closed.
- Phase 6 implementation: **complete in source; isolated ownership/apply and client-payload policy validation passed, integrated lifecycle pending** — ownership is fingerprint-scoped and acquired only by actual AMS writes, not by coincidentally matching local files. A signed omission can delete exact last-owned bytes only when the immediately prior successful AMS reconciliation used the same trusted fingerprint; server switches therefore defer exact stale cleanup instead of deleting across server contexts. Modified stale files are preserved and ownership is relinquished. Write/delete operations share PREPARED/COMMITTED, AMSTXN2 retains AMSTXN1 recovery compatibility, and ownership publishes only after COMMITTED. The server also has a fixed non-loadable `ClientPayload/plugins` tree that becomes ordinary signed P destinations, honors exclusions, bypasses server-only/client-required reclassification, and rejects destination collisions. Windows CI passed 7/7 isolated ownership/helper cases, 5/5 client-payload/prior-server policy cases, and the current-helper Phase 2 regression rerun passed 4/4. Local integrated build plus live client-only acquire/remove/rename/modified-local/pre-existing-local behavior remain before Phase 6 closes.
- Functional 2.6 validation: **partial** — a real 313.4 MiB fresh-client sync/apply/restart/reconnect and matching second preflight passed on commit `37a4125f270a417489e9d620326c4f92e808bc26`; adversarial, resource-limit, fallback, and concurrency cases remain pending.
- Transfer performance validation: **in progress** — the 16/64/32 MiB follow-up successfully moved Steam's live send-rate telemetry from the prior 8 MiB/s floor to exactly 16 MiB/s and restored the original connection settings afterward. End-to-end client payload timing for this run is still needed before treating the higher settings as a proven wall-clock improvement.
- Restart reconnect validation: **passed for the observed regression** — the patched client completed exactly one automatic character-start/reconnect sequence, then completed trusted AMS preflight and downstream mod synchronization without dispatching a second reconnect.
- Phase 3 implementation: **partial runtime validation** — a 60-file / 313.4 MiB near-bare request hit the retained cache with `prepare=0.000 s`, proving join-time cache reuse. Recognized-session disconnect cleanup, delayed background-native trust acceptance, trust rejection, normal native pointer interaction, and transient blocked-banner cleanup have passed. Startup MISS/build timing, native-dialog stale-result cleanup on mid-prompt disconnect, WAIT-HIT concurrency, invalidation, and TTL/budget eviction still require runtime validation.
- Phase 7 implementation: **complete in source; deterministic validation passed, live branded UI qualification pending** — the policy-free UI-state harness passes 6/6, the Windows branding/package harness passes, source-documentation validation passes, and existing Phase 1/3 client regression workflows remained green after the state-driven UI wiring. The maintainer has already passed the pre-renderer Phase 7 foundation dev-build and zero-delta reconnect smoke gates. The next gate is the development-only ten-state visual preview followed by a real changed-file/resume transfer to confirm runtime layout, telemetry cadence, restart/reconnect presentation, and embedded/sidecar executable icon behavior.
- Release build validation: **not yet performed**
- Public release: **not authorized**
