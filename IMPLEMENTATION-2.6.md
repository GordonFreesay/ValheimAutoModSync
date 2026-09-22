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

### Later phases

Add each 2.6-modified file here in the same phase that introduces the change, with a concise audit reason.

## Validation status

- Phase 0 repository setup: **complete**
- Phase 1 implementation: **complete in source; partial functional validation recorded**
- Functional 2.6 validation: **partial** — a real 313.4 MiB fresh-client sync/apply/restart/reconnect and matching second preflight passed on commit `37a4125f270a417489e9d620326c4f92e808bc26`; adversarial, resource-limit, fallback, and concurrency cases remain pending.
- Transfer performance validation: **in progress** — the 16/64/32 MiB follow-up successfully moved Steam's live send-rate telemetry from the prior 8 MiB/s floor to exactly 16 MiB/s and restored the original connection settings afterward. End-to-end client payload timing for this run is still needed before treating the higher settings as a proven wall-clock improvement.
- Restart reconnect validation: **passed for the observed regression** — the patched client completed exactly one automatic character-start/reconnect sequence, then completed trusted AMS preflight and downstream mod synchronization without dispatching a second reconnect.
- Release build validation: **not yet performed**
- Public release: **not authorized**
