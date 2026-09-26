# Valheim AutoModSync - mod-site package

This archive is the lightweight mod-site package for Valheim AutoModSync.

## Requirements

Install BepInExPack Valheim 5.4.2350 (or a compatible current Valheim BepInEx 5 package) separately.

This package intentionally does not bundle BepInEx or another archive.

## Installation

Extract the archive into the Valheim game or dedicated-server directory so the AutoModSync files land under:

    BepInEx/plugins/GordonFreesay-ValheimAutoModSync/

The same package contains the client role, dedicated-server role, and the apply helper. The client role disables itself inside valheim_server.exe; the server role is passive on a normal client unless that client is hosting.

Package-manager installations are treated as package-managed AutoModSync installations. AutoModSync does not overwrite its own package-manager-owned binaries through server synchronization; update AutoModSync itself through the package manager/store that installed it.

## Network behavior

AutoModSync communicates only with the Valheim server the user chooses to join, using Valheim's existing game connection. Its core function is to compare a signed server manifest, transfer missing or changed synchronized BepInEx files from that server, verify them, stage them, restart Valheim when required, and reconnect.

In 2.6, identical large client payloads can reuse one immutable cached/prewarmed server bundle; transfers are bounded by a server-wide scheduler and can resume from a retained prefix only after the server independently verifies that exact prefix. No additional synchronization port is required.

## Third-party mod redistribution

AutoModSync is only the transport mechanism. It does **not** grant redistribution rights for third-party mods selected by a server operator.

Before serving a third-party mod or related file to connecting clients, the operator is responsible for confirming that the mod's license or author permissions allow that redistribution and for complying with any conditions. A private/password-protected or noncommercial server does not by itself grant permission, and public-server operators should review every distributed mod before opening the server to unrestricted players.

Mods that prohibit redistribution, or whose permissions are unclear, should be excluded from AutoModSync transfer unless appropriate permission is obtained from the rights holder.

See: https://github.com/GordonFreesay/ValheimAutoModSync/blob/main/THIRD-PARTY-MOD-REDISTRIBUTION.md

## Security

BepInEx plugins and preloader patchers can execute code. Only approve first-contact AutoModSync trust for servers you recognize and intend to join.

First contact shows a short human-comparison security code; the complete server fingerprint remains internal for cryptographic trust/pinning. Transferred files are covered by the server's signed manifest and verified with SHA-256 before application. Ownership-safe cleanup preserves locally modified and unrelated files.

## Source

https://github.com/GordonFreesay/ValheimAutoModSync

## License

AutoModSync-authored code is MIT licensed. That license does not apply to third-party mods a server operator chooses to synchronize.
