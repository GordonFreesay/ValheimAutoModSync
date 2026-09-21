# AutoModSync 2.5.0 compatibility and signing plan

**Branch status:** core pre-handshake gate, AMS4 acknowledgement, signing pipeline, and source-documentation pass are implemented on `ai/2.5.0-compat-signing`. Live Valheim/Jotunn/Epic Loot validation is still required before release.

## Goals

1. Run AutoModSync synchronization before Valheim's normal client/server handshake reaches mod compatibility validators.
2. Avoid bypassing or weakening Jotunn, ServerSync, ValheimPlus-style checks, or other mods. After synchronization, their normal checks still run.
3. Fail open on servers that do not run AutoModSync: after a short discovery window, release the untouched vanilla handshake.
4. Preserve signed manifests, SHA-256 verification, first-contact server trust, delta transfers, staged restart, reconnect, and package-manager profile handling.
5. Prepare every AutoModSync-authored PE binary for Authenticode signing without re-signing third-party BepInEx / Unity Doorstop files.
6. Keep standalone, dedicated-server, Host & Play, and package-manager installs supported.

## Compatibility architecture

### Pre-handshake gate

2.4.8 starts AutoModSync discovery from `ZNet.SendPeerInfo`. That is too late for frameworks such as Jotunn that exchange and enforce mod-version data during `RPC_ClientHandshake` / `RPC_ServerHandshake`.

2.5.0 moves discovery to the outgoing connection's `ZNet.OnNewConnection` path and temporarily holds only the vanilla `ServerHandshake` RPC.

Flow:

```text
connection created
  -> AutoModSync registers RPCs
  -> AutoModSync sends hello
  -> vanilla ServerHandshake is held
  -> server ACKs AutoModSync immediately
  -> signed manifest is transferred
     -> match: release vanilla ServerHandshake
     -> mismatch: download, verify, stage, restart, reconnect
  -> other mods run their normal compatibility checks only after the synchronized client is ready
```

The gate is intentionally below Jotunn and other framework-specific handshake patches. It does not patch those frameworks or force them to report compatibility.

### Non-AutoModSync servers

If no AutoModSync acknowledgement/manifest is seen within the short discovery timeout (currently 3 seconds), the held `ServerHandshake` is released and the connection continues normally.

### Slow manifests

The server sends a lightweight acknowledgement before hashing/building the manifest. Once acknowledged, the client uses a longer manifest-start timeout (currently 15 seconds) instead of falling back while the server is still scanning plugins.

### Bundle transfer throughput

The original AMS4 transfer is a conservative stop-and-wait pull: one client request produces one chunk response. That is compatible but becomes slow when a bare client needs a large mod set.

2.5.0 keeps protocol version 4 and adds an optional capability in `AMS4_Ack`:

```text
bundle-window1
```

When both peers support it, the client requests up to 16 sequential chunks at a time. The server opens/seeks the prepared bundle once per requested window and sends those chunks in order from that stream. This removes most RPC round trips and per-chunk file open/seek overhead while preserving the existing chunk order, bundle SHA-256, signed manifest, exact-file verification, and final completion message.

A 2.5 client talking to an older AMS4 server automatically falls back to the original one-chunk request loop because the capability is absent. Older clients talking to a 2.5 server continue sending only the original index and therefore receive one chunk per request.

### Dependency recovery

If BepInEx skipped a server-required plugin because a hard dependency was absent, AutoModSync itself can still preflight (provided AutoModSync loaded). The missing dependency is synchronized, then the restart lets BepInEx resolve the full dependency graph normally.

## Compatibility rules

- Never suppress Jotunn/ServerSync/ValheimPlus compatibility results after preflight.
- Never patch another mod's private compatibility functions.
- Never blindly delete extra client plugins.
- Never synchronize BepInEx core/patchers as ordinary server plugins.
- Preserve unknown `version.dll` / proxy DLL safeguards.
- Keep all synchronization paths rooted under the expected BepInEx plugin/staging directories.
- Keep cryptographic manifest verification mandatory before applying transferred files.

## Signing architecture

Public signed builds will:

- sign `ValheimAutoModSync.Client.dll`
- sign `ValheimAutoModSync.Server.dll`
- sign `ValheimAutoModSync.Apply.exe`
- sign `AutoModSync.BuildTool.exe`
- verify every signature after signing and fail the release build on verification failure
- support Microsoft Artifact Signing, a CA-issued PFX, or a certificate already in the Windows certificate store
- timestamp signatures
- never commit signing credentials, PFX files, private keys, or Artifact Signing metadata
- never re-sign third-party BepInEx / Doorstop binaries

Signing improves publisher identity and SmartScreen reputation but cannot guarantee that a brand-new binary will never receive a reputation warning.

## 2.5.0 validation matrix

Before release:

| Scenario | Expected result |
| --- | --- |
| Vanilla server, no AutoModSync | Short discovery delay, then normal connection |
| AutoModSync server, already matching | Preflight match, then normal handshake |
| Missing ordinary plugin | Download -> verify -> restart -> reconnect |
| Jotunn + Epic Loot missing client-side | AutoModSync sync occurs before Jotunn validates |
| Epic Loot present but JsonDotNET dependency missing | Dependency sync -> restart -> Epic Loot loads -> Jotunn validates |
| ServerSync-based configuration mods | Their normal post-preflight behavior remains intact |
| Client has extra client-only plugins | Preserved; AutoModSync does not delete them |
| Package-manager profile | Apply/relaunch stays inside active profile |
| Dedicated server | Server role only; client role disables itself |
| Host & Play | Both roles operate without duplicate handshake dispatch |
| Non-AutoModSync server | No permanent network interception |
| Failed/invalid AMS manifest | No files applied; vanilla connection is released where safe |
| Signed release build | All AutoModSync PE files report valid Authenticode signatures |

## Release gate

Do not publish 2.5.0 until the Jotunn/Epic Loot reproduction that exposed the 2.4.8 ordering issue succeeds from a deliberately incomplete client.
