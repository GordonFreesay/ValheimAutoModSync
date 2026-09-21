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

## Security

BepInEx plugins and preloader patchers can execute code. Only trust AutoModSync server fingerprints belonging to server operators you recognize and trust.

Transferred files are covered by the server's signed manifest and verified with SHA-256 before application.

## Source

https://github.com/GordonFreesay/ValheimAutoModSync

## License

AutoModSync-authored code is MIT licensed.
