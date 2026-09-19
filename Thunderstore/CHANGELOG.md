# Changelog

## 2.4.7

- Native Thunderstore/r2modman installation from the same source used by the standalone release.
- Client role disables itself on dedicated-server processes.
- Apply helper supports package-manager installation paths while keeping persistent state under `BepInEx/AutoModSync`.
- Server signing identity is generated automatically on first launch when missing.
- Package-managed clients ignore AutoModSync-owned files advertised by older standalone servers, preventing downgrade/duplicate copies.
- Package-managed servers do not advertise stale standalone release-client payloads.
- Signed manifests, SHA-256 verification, delta synchronization, restart/reconnect, and protocol version 4 are preserved.