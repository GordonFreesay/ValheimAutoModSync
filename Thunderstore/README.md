# Valheim AutoModSync

## Join the server. The mods follow.

AutoModSync keeps a trusted Valheim server's synchronized BepInEx mod files matched on its clients.

Connect normally. AutoModSync compares the server's signed manifest with the client's installed files, transfers only missing or changed synchronized files, verifies them, stages updates, restarts Valheim when required, and reconnects.

2.5.0 can synchronize normal `BepInEx/plugins` content plus required `BepInEx/patchers` files. `BepInEx/config` files are never mirrored by default; the server must explicitly allowlist the specific config files that clients need. Arbitrary game-root/core/managed-assembly paths are not synchronization targets.

**No additional synchronization port is required.**

## Thunderstore / r2modman

Install this package with your mod manager and launch the game/server through the manager as usual.

The same package supports Client, Dedicated Server, and Host & Play. On a dedicated-server process, the client role disables itself automatically. On a normal Valheim client, the server role remains passive unless that game instance is hosting.

## First server launch

A server or host automatically creates a unique signing identity if one does not already exist:

- `BepInEx/config/ValheimAutoModSync.private.xml`
- `BepInEx/config/ValheimAutoModSync.public.xml`

Back up the private identity. Deleting it changes the server fingerprint and clients will need to trust the new fingerprint.

## Security

BepInEx plugins are executable .NET code. Only trust AutoModSync server fingerprints belonging to server operators you recognize and trust.

The server signs its synchronization manifest, and transferred files are verified against it with SHA-256.

## Source

https://github.com/GordonFreesay/ValheimAutoModSync

## Website

https://gordonfreesay.com/AutoModSync

## License

AutoModSync-authored code is MIT licensed.