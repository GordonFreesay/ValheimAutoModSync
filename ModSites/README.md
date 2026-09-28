# Valheim AutoModSync - mod-site package

This archive is the lightweight mod-site package for Valheim AutoModSync.

## Requirements

Install BepInExPack Valheim 5.4.2350 (or a compatible current Valheim BepInEx 5 package) separately.

This package intentionally does not bundle BepInEx or another archive.

## Installation

Extract the archive into the Valheim game or dedicated-server directory so the AutoModSync files land under:

    BepInEx/plugins/GordonFreesay-ValheimAutoModSync/

The same package contains the client role, dedicated-server role, and the single signed/attested `ValheimAutoModSyncInstaller.exe`. The client role disables itself inside `valheim_server.exe`; the server role is passive on a normal client unless that client is hosting.

There is **no standalone AutoModSync apply-helper executable**. The installer also provides the post-exit transactional updater mode required to replace BepInEx DLLs safely after Valheim closes.

AutoModSync core files are not treated as ordinary server-owned mods. After the 2.6.0 -> 2.6.1 migration path, a current client will not let a joined server replace its installer/updater executable. If that local core executable is missing, AutoModSync stops and asks the user to repair/reinstall AutoModSync rather than downloading or self-replacing it through a workaround.

## Network behavior

AutoModSync communicates only with the Valheim server the user explicitly chooses to join, using Valheim's existing game connection. **Server-to-client file synchronization is the core purpose of the mod**, not an auxiliary auto-update feature.

The connection flow is: authenticate to the Valheim server when required, establish/confirm the server's AutoModSync signing identity, compare its signed synchronization manifest, transfer only missing or changed synchronized BepInEx files from that joined server, verify the received bytes and paths, stage them, restart Valheim when required, and reconnect.

AutoModSync is **not a generic web downloader**. It does not crawl mod sites, fetch arbitrary URLs, contact Nexus Mods/CurseForge to acquire gameplay mods, run a background download service, or download a replacement AutoModSync updater from arbitrary network locations.

In 2.6, identical large client payloads can reuse one immutable cached/prewarmed server bundle; transfers are bounded by a server-wide scheduler and can resume from a retained prefix only after the server independently verifies that exact prefix. No additional synchronization port is required.

## Third-party mod redistribution

AutoModSync is only the transport mechanism. It does **not** grant redistribution rights for third-party mods selected by a server operator.

Before serving a third-party mod or related file to connecting clients, the operator is responsible for confirming that the mod's license or author permissions allow that redistribution and for complying with any conditions. A private/password-protected or noncommercial server does not by itself grant permission, and public-server operators should review every distributed mod before opening the server to unrestricted players.

Mods that prohibit redistribution, or whose permissions are unclear, should be excluded from AutoModSync transfer unless appropriate permission is obtained from the rights holder.

See: https://github.com/GordonFreesay/ValheimAutoModSync/blob/main/THIRD-PARTY-MOD-REDISTRIBUTION.md

## Security

BepInEx plugins and preloader patchers can execute code. Only approve first-contact AutoModSync trust for servers you recognize and intend to join.

First contact shows a short human-comparison security code; the complete server fingerprint remains internal for cryptographic trust/pinning. Transferred files are covered by the server's signed manifest and verified with SHA-256 before application. Ownership-safe cleanup preserves locally modified and unrelated files.

When synchronized files must change, AutoModSync launches its already-installed `ValheimAutoModSyncInstaller.exe` in a **visible updater mode**. The updater waits for Valheim to close normally; AutoModSync does not force-kill Valheim or another process. Ordinary synchronization runs the updater as the current user without requesting administrator elevation. The existing PREPARED/COMMITTED transaction, verified backups, rollback/recovery, path/reparse checks, and hash verification remain in effect.

## Source

https://github.com/GordonFreesay/ValheimAutoModSync

## License

AutoModSync-authored code is MIT licensed. The AutoModSync package itself contains no arbitrary third-party gameplay mods. The MIT license does not apply to third-party mods a server operator independently chooses to synchronize from their Valheim server.
