# AutoModSync 2.6.1 security-hardening release record

This document records the approved scope and implemented security/compatibility architecture for Valheim AutoModSync **2.6.1**. Live qualification evidence is recorded in `TESTING-2.6.md`.

## Release purpose

**2.6.0 was the feature release. 2.6.1 is the released security and connection-boundary hardening update for the 2.6 line.**

The 2.6.1 scope was intentionally limited to security changes and compatibility work required to make the protected-server/preflight model reliable; unrelated product features, provider acquisition flows, new synchronization roots, broad UI work, and transfer redesigns remained deferred.

Predecessor public release: **2.6.0**. Current release: **2.6.1**. Protocol remains **AMS4 / protocol 4**; newer behavior is negotiated with capabilities/RPCs.

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

## Single installer/updater executable

2.6.1 removes the standalone `ValheimAutoModSync.Apply.exe` component entirely.

The runtime/distribution contract is now:

- `ValheimAutoModSync.Client.dll`;
- `ValheimAutoModSync.Server.dll`; and
- one `ValheimAutoModSyncInstaller.exe`, which owns install/repair/uninstall **and** the post-exit transactional apply/recovery path.

The client never replaces loaded BepInEx DLLs in-process. After verified staging and durable pending state are written, it launches the installer with the private `--apply-pending` mode, requests a normal Valheim disconnect/quit, and exits. The updater presents a visible ordinary Windows UI and waits passively for that exact Valheim process to exit.

Security/trust requirements:

- no AutoModSync production source calls `Process.Kill` or `.Kill()` on another process;
- there is no timeout that force-terminates Valheim;
- runtime updater mode runs as the invoking user (`asInvoker`) and does not prompt for elevation;
- explicit interactive install/repair/uninstall requests normal Windows UAC through `runas` only when needed;
- updater execution is visible, not a hidden/background helper process;
- the existing PREPARED/COMMITTED journal, durable backups, rollback/recovery, path/reparse checks, pre/post-write digests, and ownership transition remain the apply authority;
- no packer, obfuscator, self-decrypting payload, custom preloader, or custom bootstrap loader is introduced.

For the one-time 2.6.0 -> 2.6.1 migration, a standalone 2.6.1 server's authenticated release payload contains both the 2.6.1 client DLL and the installer/updater. On a public server, a 2.6.0 client can stage both directly using its existing 2.6.0 apply path. On a password-protected server, released 2.6.0 cannot speak `password-auth2` and also has a fixed short preflight timeout, so the server uses a constrained two-stage bridge: the first connection receives no protected AMS state and performs Valheim's normal password verification; after Valheim accepts that exact peer, the server records a five-minute, memory-only, one-use migration grant bound to a SHA-256 of that platform peer identity, discards quarantined gameplay/mod traffic, and closes the bootstrap connection. The same 2.6.0 identity reconnects once to consume that grant for synchronization only. Migration authorization cannot pass `ServerHandshake` into gameplay. After the old helper stages the 2.6.1 client DLL plus installer and restarts Valheim, the 2.6.1 client uses the normal exact-connection `password-auth2` boundary and only the installer/updater; it never falls back to legacy `Apply.exe`.

Once the 2.6.1 client is running, `ValheimAutoModSyncInstaller.exe` becomes **local AMS core**, not server-owned mod content. The 2.6.1 client ignores later server attempts to replace that executable and refuses the join with a repair/reinstall message if the local updater is missing. This intentionally avoids self-updating a running executable, temporary executable copies, reboot rename tricks, shell scripts, or other behavior that would reduce scanner/user trust.

Nexus Mods and CurseForge are the intended mod-site package targets for this release. Thunderstore publication is not part of 2.6.1 release qualification unless its redistribution/policy fit is confirmed separately.

## Private signing-key ACL hardening

2.6.1 treats `ValheimAutoModSync.private.xml` as a server credential rather than ordinary configuration.

On Windows the shared `AutoModSyncPrivateKeySecurity` primitive:

- disables ACL inheritance on the private-key file;
- replaces broad/inherited access with explicit Full Control for the current Windows process identity, LocalSystem, and local Administrators only;
- verifies the resulting DACL before the key may be used;
- is linked into the server, installer, and BuildTool identity-generation paths.

New keys created by installer/BuildTool are secured immediately. Existing identities are re-hardened by the actual server process at startup so upgrades converge on the real runtime account. A key that cannot be secured/verified is not used for signing; a newly generated identity is deleted if hardening fails.

This is deliberately ACL hardening rather than DPAPI/CNG migration in 2.6.1, avoiding machine/account-lock-in and backup/export incompatibilities immediately before release.

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
- A genuine client `Disconnect` is never trapped behind the quarantine, so cancellation/timeouts can close the connection immediately.
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
- A recognized preflight that does not progress and has no active/queued bundle transfer is closed after a bounded 30-minute lifetime instead of retaining authentication/quarantine state indefinitely.

A current AMS peer cannot invoke `ServerHandshake` early to bypass this ordering; current peers must reach AMS readiness first.

This is generic by design. AMS does not special-case Warfare, ServerSync, Jotunn, Epic Loot, or another individual mod.

## First-contact trust and redistribution-responsibility boundary

The native first-contact trust prompt is a **client security decision**, not a licensing certification. It states that:

- the server is asking permission to install/update executable BepInEx mod files;
- synchronized mods are executable code and should be accepted only from a trusted server operator;
- choosing **Yes** authorizes that trusted server to provide synchronized executable files/configuration to this PC; and
- third-party content is provided by the server operator, while AutoModSync does not determine or verify third-party licensing or redistribution rights.

The joining player is **not** asked to represent that they personally verified redistribution permission for the server's mod set. The server operator selects and distributes those files and is responsible for ensuring that every synchronized third-party file may be redistributed under its applicable license/permissions.

## Release qualification matrix

The 2.6.1 release was not tagged until the required static and live qualification covered:

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
- user/client cancellation during preflight disconnects immediately instead of being queued;
- an inactive recognized preflight cannot retain auth/quarantine state beyond the configured hard lifetime;
- existing 2.6.0 transfer/cache/resume/scheduler/path/apply/ownership regressions remain green;
- final release/store archives contain no `ValheimAutoModSync.Apply.exe`;
- the exact release installer/updater performs a live apply/restart/reconnect successfully without elevation or forced process termination;
- a real 2.6.0 -> 2.6.1 upgrade receives the installer/updater before the 2.6.1 client needs it.

## Post-release scope

2.6.1 completes the planned Windows 2.x feature set. The 2.x line is now maintenance-only: bug fixes, security fixes, compatibility fixes, and release-integrity corrections may be shipped when necessary, but no additional product features are planned.

Previously considered provider handoff/acquisition, verify-only mod requirements, broad UI expansion, new synchronization roots, new transfer/protocol features, and similar product expansion are no longer on the roadmap. AutoModSync intentionally remains a synchronization/integrity tool; third-party mod selection and redistribution responsibility remain with the server operator.

The only contemplated future feature line is an optional Linux / Steam Deck port under 3.0. It is unscheduled, not promised, and may never be developed. See `ROADMAP.md`.

## Release result

The release rule was satisfied before `v2.6.1` was tagged: static gates were supplemented by real Valheim/BepInEx validation, including the protected server-access boundary, stale-mod preflight isolation, the released-2.6.0 migration bridge, updater apply/restart/reconnect, and a normal post-migration 2.6.1 join. See `TESTING-2.6.md` for the dated evidence.
