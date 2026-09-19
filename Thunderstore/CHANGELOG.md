# Changelog

## 2.4.8

- Fixes Thunderstore/r2modman profile state-root handling during synchronization.
- Saves the original package-manager launch context before restart.
- Relaunches Valheim through Steam with the original Doorstop/BepInEx profile arguments so the replacement process remains modded.
- Restores automatic one-shot reconnect to the server after synchronized files are applied.
- Automatically continues through the previously selected character during reconnect.
- Prevents duplicate reconnect dispatches and false reconnect timeout warnings.
- Standalone/manual installation behavior and protocol version 4 remain unchanged.
## 2.4.7

- Native Thunderstore/r2modman installation from the same source used by the standalone release.
- Client role disables itself on dedicated-server processes.
- Apply helper supports package-manager installation paths while keeping persistent state under `BepInEx/AutoModSync`.
- Server signing identity is generated automatically on first launch when missing.
- Package-managed clients ignore AutoModSync-owned files advertised by older standalone servers, preventing downgrade/duplicate copies.
- Package-managed servers do not advertise stale standalone release-client payloads.
- Signed manifests, SHA-256 verification, delta synchronization, restart/reconnect, and protocol version 4 are preserved.