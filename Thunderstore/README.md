# Valheim AutoModSync

## Join the server. The mods follow.

AutoModSync keeps a trusted Valheim server's synchronized BepInEx mod files matched on its clients.

Connect normally. AutoModSync compares the server's signed manifest with the client's installed files, transfers only missing or changed synchronized files, verifies them, stages updates, restarts Valheim when required, and reconnects.

2.6.0 synchronizes normal `BepInEx/plugins` content plus required `BepInEx/patchers` files and explicitly allowlisted `BepInEx/config` files. Dedicated servers can also place client-required files under `BepInEx/AutoModSync/ClientPayload/plugins/**` so those files are distributed without being loaded by the server itself. Arbitrary game-root/core/managed-assembly paths are not synchronization targets.

**No additional synchronization port is required.**


## 2.6 highlights

- Large fresh-client syncs reuse cached/prewarmed immutable bundles instead of rebuilding the same ZIP per client.
- Server-wide scheduling bounds active/queued transfers and shares an aggregate bandwidth budget fairly.
- Interrupted downloads can resume from a server-verified exact prefix.
- Transactional apply/rollback protects live files across interrupted updates.
- Ownership-safe stale removal preserves locally modified and unrelated files.
- The branded synchronization UI shows comparison, queue, transfer, verification, restart, reconnect, and resume telemetry.
- Steam-backed servers can advertise a passive AMS presence marker so AMS clients can show a small server-browser badge.

## Thunderstore / r2modman

Install this package with your mod manager and launch the game/server through the manager as usual.

The same package supports Client, Dedicated Server, and Host & Play. On a dedicated-server process, the client role disables itself automatically. On a normal Valheim client, the server role remains passive unless that game instance is hosting.

## First server launch

A server or host automatically creates a unique signing identity if one does not already exist:

- `BepInEx/config/ValheimAutoModSync.private.xml`
- `BepInEx/config/ValheimAutoModSync.public.xml`

Back up the private identity. Deleting it changes the server fingerprint and clients will need to trust the new fingerprint.

## Security

BepInEx plugins and preloader patchers can execute code. Only approve first-contact AutoModSync trust for servers you recognize and intend to join.

First contact shows a short security code derived from the server signing identity; the complete fingerprint remains internal for cryptographic pinning. The server signs its synchronization manifest, and transferred files are verified against it with SHA-256. Interrupted 2.6 transfers can retain a bounded verified prefix and resume only after the server independently verifies that exact prefix.

## Source

https://github.com/GordonFreesay/ValheimAutoModSync

## Website

https://gordonfreesay.com/AutoModSync

## License

AutoModSync-authored code is MIT licensed.