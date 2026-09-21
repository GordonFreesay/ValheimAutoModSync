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

The held RPC is replayed with the **exact argument array supplied by Valheim**. AutoModSync must never manufacture a parameterless `ServerHandshake`: current Valheim builds can attach handshake data, and dropping those arguments causes the server's registered RPC decoder to read beyond the received package.

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

2.5.0 first added `bundle-window1`, which lets the client request up to 16 sequential chunks at a time. Live testing showed that sending those chunks as separate Base64 RPC messages was still too slow for a bare-client 32 MiB synchronization.

The preferred 2.5.0 path is now the additional optional capability:

```text
bundle-batch1
```

When both peers support it, the server reads a bounded window from the prepared ZIP and packs up to roughly 384 KiB of **raw binary chunk data** into one `AMS4_BundleBatch` RPC. This removes Base64's ~33% wire expansion and collapses many RPC messages into one reliable Valheim/Steam message. Chunk ordering, final bundle SHA-256, signed-manifest verification, exact-file verification, staging, and completion semantics remain unchanged.

Fallback order is: `bundle-batch1` -> `bundle-window1` -> original single-chunk AMS4. Older AMS4 clients and servers therefore continue to interoperate without a protocol-version bump.

Live testing on a gigabit LAN isolated a second bottleneck below AutoModSync's framing: Valheim's Steam transport pins `SendRateMax` near 153600 B/s, which closely matches the observed ~0.1-0.15 MiB/s transfer ceiling even when raw TCP/iperf reaches line rate. During an AutoModSync bundle only, the server therefore raises **only the specific peer connection's** Steam `SendRateMax` (default target 8 MiB/s) and restores its previous value when the transfer completes or aborts. `SendRateMin` is deliberately untouched so Steam congestion control can still reduce the rate on weak links. Non-Steam/PlayFab paths simply skip this optimization.

### Synchronized BepInEx roots

2.5.0 no longer assumes every required mod file lives under `BepInEx/plugins`. The signed manifest uses fixed kind codes with fixed destinations:

```text
P -> BepInEx/plugins
R -> BepInEx/patchers
C -> BepInEx/config
```

`plugins` and `patchers` are scanned recursively. Patchers are installed by the out-of-process apply helper before Valheim restarts, so preloader patchers are present before the next BepInEx preloader pass.

`config` is deliberately different: it is **not mirrored by default**. The server must explicitly opt files in through `Compatibility.SyncConfigPatterns`. This avoids overwriting client keybind/UI/machine-local settings or accidentally distributing unrelated server configuration. `ValheimAutoModSync.private.xml` is hard-blocked even if a broad allowlist pattern would otherwise match.

Server/client side classification is explicit rather than guessed from mod metadata because there is no universal BepInEx side marker across the Valheim ecosystem. `Compatibility.ServerOnlyPatterns` excludes server-only plugin/patcher files. `Compatibility.ClientRequiredPatterns` is optional; when empty, every non-excluded/non-server-only plugin/patcher file remains client-required, preserving existing behavior. When set, only matching plugin/patcher files are advertised.

The hello advertises the optional `roots1` client capability. Plugin-only AMS4 interoperability remains intact with older peers; a server whose signed manifest actually contains patcher/config entries requires a roots-capable 2.5 client rather than silently installing those files under the wrong root.

AutoModSync still does **not** remotely synchronize `BepInEx/core`, game-root proxy/bootstrap DLLs, `Valheim_Data/Managed`, or arbitrary filesystem paths. Those remain outside the synchronization trust boundary.

### Dependency recovery

If BepInEx skipped a server-required plugin because a hard dependency was absent, AutoModSync itself can still preflight (provided AutoModSync loaded). The missing dependency is synchronized, then the restart lets BepInEx resolve the full dependency graph normally.

## Compatibility rules

- Never suppress Jotunn/ServerSync/ValheimPlus compatibility results after preflight.
- Never patch another mod's private compatibility functions.
- Never blindly delete extra client plugins.
- Never synchronize BepInEx core, game-root proxy/bootstrap DLLs, or managed game assemblies through the mod manifest.
- Synchronize patchers only to the dedicated `BepInEx/patchers` root; synchronize config only through the explicit config allowlist.
- Preserve unknown `version.dll` / proxy DLL safeguards.
- Keep all synchronization paths rooted under the fixed BepInEx plugin/patcher/config and AutoModSync staging directories.
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
| Required preloader patcher | Signed `R` entry -> stage -> apply to `BepInEx/patchers` -> restart before preloader runs |
| Explicitly allowlisted config | Signed `C` entry -> stage -> apply to `BepInEx/config`; non-allowlisted configs remain local |
| Server-only plugin/patcher pattern | Excluded from client manifest while remaining installed on the server |
| Client has extra client-only plugins | Preserved; AutoModSync does not delete them |
| Package-manager profile | Apply/relaunch stays inside active profile |
| Dedicated server | Server role only; client role disables itself |
| Host & Play | Both roles operate without duplicate handshake dispatch |
| Non-AutoModSync server | No permanent network interception |
| Failed/invalid AMS manifest | No files applied; vanilla connection is released where safe |
| Signed release build | All AutoModSync PE files report valid Authenticode signatures |

## Release gate

Do not publish 2.5.0 until the Jotunn/Epic Loot reproduction that exposed the 2.4.8 ordering issue succeeds from a deliberately incomplete client.
