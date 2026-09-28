# Valheim AutoModSync 2.6.1

AutoModSync 2.6.1 is the **security and connection-boundary hardening release for the 2.6 line**. It keeps **AMS4 / protocol 4** and the 2.6.0 transfer/cache/resume/apply/ownership model.

## One installer/updater, no Apply.exe

2.6.1 removes the standalone `ValheimAutoModSync.Apply.exe` helper.

`ValheimAutoModSyncInstaller.exe` now also owns the existing crash-safe post-exit apply/recovery transaction:

- the updater is visible rather than hidden;
- it waits for Valheim to close normally and **never force-terminates the game**;
- normal sync updates run as the current user without a UAC prompt;
- explicit install/repair/uninstall still uses normal Windows elevation when required;
- PREPARED/COMMITTED journaling, verified backups, rollback/recovery, path/reparse validation, hashes, and ownership rules are preserved;
- no packer, obfuscator, self-decrypting payload, or custom loader was added.

Standalone servers also provide the signed installer/updater in the 2.6.1 migration payload so an existing 2.6.0 client can receive it before the new client DLL depends on it. After that migration, the 2.6.1 client treats the updater as local AutoModSync core and does not let a game server replace the running updater. A missing updater requires an AutoModSync repair/reinstall instead of invoking a self-update workaround.

The intended 2.6.1 mod-site packages are Nexus Mods and CurseForge. Thunderstore publication is deferred unless its policy fit is confirmed separately.

## Password-protected servers

Password-protected servers now withhold protected AutoModSync synchronization state until the **exact live connection** proves the correct Valheim password.

Before authentication, AMS exposes only product presence/version/protocol, the fact that authentication is required, and an opaque random one-time challenge. It does **not** expose the server signing identity/fingerprint, manifest metadata/content, synchronized paths, hashes, sizes, configuration, bundle/cache state, transfer capabilities, or synchronized bytes.

2.6.1 replaces the earlier reusable-verifier proof design with **`password-auth2`**:

- Valheim's normal password dialog remains the input UI.
- The client derives Valheim's salted password verifier locally.
- AMS sends a one-time **HMAC-SHA256 challenge response** instead of transmitting that reusable verifier.
- The server challenge is random, exact-connection scoped, one-use, and time-bounded.
- Authorization/challenge state is discarded when the connection ends.

A restart/reconnect must authenticate normally again before protected AMS synchronization can resume.

## Stale-mod compatibility preflight

2.6.1 also fixes a class of failures where an older client mod can reject/disconnect before AMS gets a chance to update it.

Some compatibility libraries send their own version RPCs directly from `ZNet.OnNewConnection`, earlier than Valheim's normal `ServerHandshake`. Holding only `ServerHandshake` therefore was not sufficient.

Current 2.6.1 peers negotiate **`preflight-quarantine1`**:

- client and server temporarily quarantine non-AMS compatibility RPCs during AMS preflight;
- AMS protocol/auth traffic continues normally;
- core server denial/password controls are not delayed;
- if files need updating, stale queued compatibility traffic is discarded with the old connection;
- after a verified no-change preflight, `AMS4_Ready` releases the queues in their original invocation order;
- non-AMS/legacy flows retain bounded fail-open/handshake fallback behavior;
- genuine client disconnects bypass the quarantine immediately; and
- recognized preflights with no active/queued transfer cannot retain auth/quarantine state indefinitely (30-minute hard lifetime).

This is generic rather than Warfare/ServerSync-specific.

## Filesystem and apply safety

2.6.1 retains the 2.6.0 hardening already present in the delivery path: signed manifests, fixed synchronization roots, canonical path validation, traversal/reserved-name defenses, reparse-point rejection, bounded archive/resource handling, SHA-256 verified staging, fingerprint-scoped ownership/deletion, and the installer-linked journaled transaction engine with backup/rollback and pre/post-write digest verification.

## First-contact trust

The first server-fingerprint trust dialog now makes the executable-code boundary explicit: BepInEx mods are executable code and can act with the permissions of the user's Valheim process/account. Users should accept files only from a server operator they trust.

Choosing **Yes** authorizes that server to provide synchronized executable mod files and configuration to the user's PC. The joining player is **not** asked to certify that the server has redistribution rights for its third-party mod set.

Third-party content is provided by the server operator. AutoModSync does not determine or verify third-party licensing or redistribution rights; the server operator is responsible for ensuring each synchronized third-party file may be redistributed.

## Compatibility

Public/no-password servers continue using AMS4/protocol 4.

AutoModSync 2.6.0 and earlier clients cannot use the new password-authentication mechanism on password-protected 2.6.1 servers and fail closed rather than receiving protected synchronization data.

## Security reporting

The repository now includes a `SECURITY.md` policy defining the AMS threat boundary, what should be reported as a vulnerability, and the preferred private-disclosure path.

## Release qualification

Because password authentication and compatibility quarantine alter live connection ordering, v2.6.1 should not be tagged/published until the password matrix and stale-client compatibility case have been reproduced successfully on a real Valheim/BepInEx setup.
