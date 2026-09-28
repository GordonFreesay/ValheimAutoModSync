# AutoModSync 2.6.1 security-hardening plan

This document is the authoritative scope for Valheim AutoModSync **2.6.1**.

## Release purpose

**2.6.0 is the feature release. 2.6.1 is the security and connection-boundary hardening release for the 2.6 line.**

2.6.1 should not add unrelated product features, transfer tuning, new synchronization roots, provider acquisition flows, or broad UI work. It may include security changes and compatibility fixes that are necessary to make the security/preflight model reliable.

Baseline public release: **2.6.0**. Protocol remains **AMS4 / protocol 4**; new behavior is negotiated with capabilities/RPCs.

## Security model

AutoModSync assumes that the server operator and protocol implementation are fully knowable to an attacker. Security must not depend on hiding or obfuscating the AMS binaries.

The important boundaries are:

- cryptographic server identity and signed manifest integrity;
- explicit first-contact user trust;
- password/authentication state scoped to the exact live connection;
- no protected server synchronization disclosure before required authentication;
- fixed client filesystem roots and canonical path validation;
- no reparse-point traversal;
- bounded network/archive/resource handling;
- hash-verified staging and transactional apply;
- conservative, server-fingerprint-scoped ownership/deletion;
- compatibility handshakes must not run against stale client mods before AMS has had the opportunity to update them.

BepInEx plugins are executable code. AutoModSync can verify **who supplied bytes and whether they were altered**; it cannot make intentionally malicious plugin code safe. The trust prompt therefore warns users that synchronized BepInEx mods execute with the permissions of their Valheim process/user account.

## Existing 2.6 defenses retained

2.6.1 keeps the 2.6.0 delivery hardening:

- signed manifests and pinned server identity;
- client-independent file/count/expanded/archive ceilings;
- canonical fixed-root P/R/C destinations;
- traversal, rooted-path, reserved-name, trailing-dot/space, and reparse-point rejection;
- streaming extraction of only signed expected entries;
- SHA-256 verification before staging/apply;
- protected config exclusions including AMS identity files and `BepInEx.cfg`;
- fingerprint-scoped ownership with digest-checked stale deletion;
- journaled PREPARED/COMMITTED apply with backup, rollback/recovery, pre-write and post-write digest verification;
- fail-closed behavior after a server is positively recognized as AMS.

## Password-protected server boundary

A password-protected server must not disclose protected AMS state or synchronized bytes until the exact live connection proves the correct Valheim password.

Pre-authentication AMS may reveal only:

- AutoModSync presence;
- product version / protocol;
- that authentication is required; and
- an opaque one-time random authentication challenge.

It must not reveal signing identity/fingerprint, manifest dimensions/content, filenames/paths, hashes, sizes, synchronized configuration, bundle/cache state, transfer capabilities, or synchronized bytes.

Every protected manifest/bundle entry point re-checks authorization server-side.

### Non-replayable password proof

2.6.1 negotiates **`password-auth2`**.

For a passworded server:

1. Server creates a cryptographically random 32-byte nonce for the exact `ZRpc` and sends it in an `auth-required2` presence-only ACK.
2. Valheim's normal password dialog remains the user-input UI.
3. Client locally derives Valheim's normal salted password verifier.
4. AMS sends **HMAC-SHA256(verifier, domain || nonce)** rather than sending the reusable verifier itself.
5. Server computes the expected HMAC from its existing salted verifier and the stored nonce.
6. Challenge state is consumed before comparison, expires after 15 minutes, and is bound to the exact live `ZRpc`.
7. Disconnect removes challenge/authorization state.

This prevents an observed AMS authentication response from being replayed as the reusable Valheim password verifier.

The plaintext password is retained only temporarily in the client process when necessary to replay Valheim's normal post-sync `PeerInfo` without a second prompt; it is cleared on completion/failure/disconnect/restart. A restart/reconnect requires normal authentication again.

## Preflight compatibility isolation

Holding only Valheim's `ServerHandshake` is insufficient. Libraries such as ServerSync can send custom version RPCs directly from `ZNet.OnNewConnection`, before the vanilla handshake. A stale client mod can therefore reject/disconnect before AMS downloads its replacement.

2.6.1 negotiates **`preflight-quarantine1`** and adds `AMS4_Ready`.

### Client side

At highest-priority connection prefix, AMS arms a bounded quarantine before ordinary third-party connection prefixes execute.

While AMS preflight owns the exact connection:

- AMS4 traffic is allowed.
- Other outbound RPCs, including third-party version/compatibility RPCs and Valheim `ServerHandshake`, are queued in original invocation order.
- Queue length is bounded; overflow fails closed for a recognized AMS session.
- If the endpoint is not AMS, the existing short discovery timeout fails open and replays the queued calls unchanged.
- If files must change, the old process never releases the stale queue; restart/reconnect loads the new mod set.
- If no files need changing, the current client sends `AMS4_Ready`, then replays its queued calls in original order.

### Server side

Before ordinary third-party `OnNewConnection` prefixes can send version traffic, AMS arms a per-`ZRpc` outbound quarantine.

- AMS4 messages are allowed through.
- Core server denial/password controls (`Error`, `Disconnect`, `ClientHandshake`) are never delayed.
- Other early server RPCs are queued and bounded.
- For current peers, `AMS4_Ready` releases the queue only after the password/disclosure gate is satisfied.
- For legacy/non-AMS flows, normal `ServerHandshake` (or a bounded non-AMS timeout) releases the queue.
- Disconnect discards queued stale traffic.

A current AMS peer cannot invoke `ServerHandshake` early to bypass this ordering; current peers must reach AMS readiness first.

This is generic by design. AMS does not special-case Warfare, ServerSync, Jotunn, Epic Loot, or another individual mod.

## First-contact trust and licensing boundary

The native first-contact trust prompt states that:

- the server is asking permission to install/update executable BepInEx mod files;
- synchronized mods are executable code and should be accepted only from a trusted server operator;
- choosing **Yes** confirms permission to receive the server-provided mods/configuration; and
- AutoModSync does not verify or enforce third-party licensing or redistribution requirements.

## Required regression matrix before release

2.6.1 cannot be tagged/published until tests cover:

- no password submitted: zero protected AMS metadata/bytes;
- wrong password: zero protected AMS metadata/bytes;
- correct password: protected discovery begins only after accepted authentication;
- captured/replayed old auth response does not authenticate a new challenge/connection;
- early/malicious AMS bundle RPC cannot bypass auth;
- connection A authorization cannot authorize connection B;
- disconnect/reconnect revokes auth and requires normal authentication again;
- no-password server still follows normal AMS discovery/sync;
- non-AMS server still receives its original RPC/handshake flow after the short fail-open window;
- stale client Warfare/ServerSync vs newer server Warfare can reach AMS, update, restart, reconnect, then pass compatibility;
- current matching ServerSync/Jotunn-style peers still pass compatibility after AMS readiness;
- changed-sync path never replays stale pre-update compatibility RPCs;
- queue overflow is bounded/fail-closed for recognized AMS sessions;
- existing 2.6.0 transfer/cache/resume/scheduler/path/apply/ownership regressions remain green.

## Explicitly deferred

Unless a new issue is itself a security boundary or necessary compatibility fix for that boundary, defer it to 2.6.2+:

- provider handoff/acquisition UX;
- verify-only required mods;
- broad UI redesign;
- new transfer algorithms/tuning;
- new synchronization roots;
- packaging/store workflow redesign;
- unrelated cleanup/refactors.

## Release rule

Do not merge, tag, or publish v2.6.1 based only on static source gates. The password path and preflight quarantine both alter live connection ordering and require real Valheim/BepInEx validation first.
