# AutoModSync 2.6.1 patch-release plan

This document is the authoritative planning baseline for Valheim AutoModSync **2.6.1**.

## Release purpose

**2.6.1 is a single-purpose access-control patch over the released 2.6.0 behavior.**

Except for the password-protected-server boundary described below, 2.6.1 must behave like 2.6.0. Do not use this release to bundle unrelated safety fixes, UI changes, packaging changes, release-pipeline changes, transfer changes, licensing features, or cleanup work.

The release should be reviewable as:

> **2.6.0 + password-authenticated AutoModSync access on password-protected servers + unavoidable version/release metadata.**

## Baseline and invariants

- Baseline public release: **2.6.0**.
- 2.6.0 release tag commit: `0da61e625220766aca6c47bd990e063d000d3bea`.
- Planned release: **2.6.1**.
- Network protocol remains **AMS4 / protocol 4** unless implementation proves a wire change is absolutely required; protocol churn is not a goal of this patch.
- The existing `v2.6.0` tag, release ZIP, checksum, and attestation remain immutable historical artifacts.
- 2.6.1 receives its own version, tag, exact build artifacts, hashes, attestations, release notes, and publication.
- Runtime/package behavior not required for the password boundary should remain equivalent to released 2.6.0.
- Prefer implementing and qualifying 2.6.1 from the released 2.6.0 baseline so unrelated post-2.6.0 `main` work cannot accidentally enter the patch.

## The only runtime change: authenticate before protected AMS access

Password-protected Valheim servers must treat successful server-side Valheim password authentication as an access-control boundary for AutoModSync.

A peer that can reach the server socket but has **not** supplied the correct server password must not be able to download synchronized mods or obtain protected synchronization information.

### Required behavior

On a password-protected server, before the **server** has accepted the Valheim password for that exact live connection, AutoModSync must not disclose or serve:

- the server signing identity/fingerprint or trust material;
- the signed synchronization manifest;
- synchronized paths or filenames;
- file hashes or file sizes;
- bundle identifiers or bundle contents;
- synchronized configuration contents;
- cache/build metadata that reveals synchronized content; or
- any other protected synchronization state.

Most importantly, **no synchronized mod/configuration bytes may be downloadable before successful password authentication**.

Enforcement must be server-side. A modified client that sends `AMS4_Hello`, manifest requests, bundle requests, resume requests, or any other AMS RPC before authentication must not bypass the boundary.

If a pre-authentication AMS response is needed for compatibility, it must be minimal and reveal no protected synchronization state. An `AMS present / authentication required` capability response is acceptable if necessary.

The passive Steam server-browser `automodsync` / `automodsync_protocol` presence advertisement may remain public because it exposes only product capability/version information.

Authorization must be bound to the exact live connection/`ZRpc` generation:

- successful authentication authorizes only that connection;
- disconnect immediately revokes the authorization;
- a later connection cannot inherit authorization from an earlier peer;
- restart/reconnect must pass through Valheim's normal password authentication again before AMS disclosure or transfer resumes.

No-password/public servers retain the released 2.6.0 AMS behavior.

### Required ordering

For password-protected servers, the target ordering is:

```text
transport established
  -> normal Valheim password challenge
  -> server accepts the correct password for this exact connection
  -> AutoModSync discovery/trust/manifest/compare/transfer
  -> normal third-party mod compatibility validation
  -> normal join
```

The implementation must preserve the reason AMS moved ahead of the normal Valheim `ServerHandshake` in 2.5: Jotunn/Epic Loot/other compatibility checks must not reject a client before required synchronization has had a chance to complete.

Therefore the implementation must identify the earliest reliable point where the server has accepted the password **while still holding back the compatibility stage that must occur after AMS synchronization**.

### Required regressions

2.6.1 cannot release until live/deterministic tests demonstrate:

- **No password submitted:** zero protected AMS metadata and zero synchronized bytes are disclosed.
- **Wrong password:** zero protected AMS metadata and zero synchronized bytes are disclosed.
- **Correct password:** AMS becomes available only after the server accepts the password, and normal synchronization can proceed.
- **Malicious/early AMS RPC:** deliberately sending AMS requests before authentication reveals no protected state and transfers no bytes.
- **Connection isolation:** authentication of connection A cannot authorize connection B.
- **Disconnect revocation:** closing an authenticated connection immediately removes its AMS authorization.
- **Reconnect:** a restarted/reconnected client must authenticate normally again before AMS can resume.
- **No-password server:** released 2.6.0 fail-open discovery/synchronization behavior remains unchanged.
- **Compatibility:** existing AMS4/Jotunn-style handshake-order behavior remains valid; the patch must not reintroduce the pre-2.5 compatibility problem.
- **2.6.0 regression:** existing 2.6.0 transfer/cache/resume/scheduler/apply/ownership behavior remains otherwise unchanged.

## Release implementation rule

Do not opportunistically fix unrelated issues while implementing this boundary.

Allowed changes are limited to:

1. the minimum client/server connection-state changes required to place AMS behind successful password authentication on password-protected servers;
2. focused tests/harness changes proving the boundary;
3. comments/docs directly explaining the boundary;
4. the normal version bump to 2.6.1 and version-specific release notes/metadata required to build and publish a distinct release.

If implementation uncovers an unrelated bug, record it for 2.6.2+ rather than expanding 2.6.1 unless it makes the password boundary impossible to implement safely.

## Explicitly deferred to 2.6.2 and beyond

The following previously discussed work is **not part of 2.6.1**:

- removing the Apply helper's forced Valheim termination fallback;
- fixing stale first-contact trust dialogs after connection timeout/failure;
- adding redistribution-policy files to every release package;
- changing store publication to promote canonical Distribution Packages artifacts instead of recompiling;
- broader patch-release/readiness refactoring;
- deterministic/reproducible compiler work;
- expanded Defender/Microsoft release-gate changes beyond the release process already used for 2.6.0;
- verify-only / do-not-transfer required mods;
- provider handoff/acquisition UX for Nexus, Thunderstore, CurseForge, or other authorized sources;
- broad UI redesign;
- new transfer algorithms, throughput tuning, synchronization roots, or protocol features.

Those items are tracked separately for **2.6.2+** so this access-control correction can be released independently.

## Recommended implementation/release order

1. Start from the exact released 2.6.0 baseline or otherwise prove the candidate contains no unrelated post-2.6.0 runtime/package changes.
2. Reproduce the current behavior on a password-protected test server.
3. Identify the exact Valheim server-side password-acceptance point and the compatibility handshake ordering around it.
4. Implement the minimum per-connection authorization gate.
5. Add the password-boundary regression matrix.
6. Rerun the relevant existing 2.6.0 regression/live gates to prove no unrelated behavior changed.
7. Bump only required version/release metadata to 2.6.1.
8. Freeze the candidate.
9. Tag **`v2.6.1`** and build/verify the exact release artifacts using the existing release process.
10. Publish 2.6.1 as its own release.
11. Resume deferred work under 2.6.2+.

## Release-note headline

The 2.6.1 release notes should be intentionally narrow:

> **AutoModSync 2.6.1 closes a password-protected-server access-control gap.** On servers that require a Valheim password, AutoModSync will not expose synchronization details or allow synchronized mods/configuration files to be downloaded until the server has accepted the correct password for that connection. Other AutoModSync behavior remains aligned with 2.6.0.

Password protection still does **not** itself grant a server operator redistribution rights for third-party mods; that separate licensing responsibility remains unchanged.
