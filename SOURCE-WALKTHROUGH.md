# Source walkthrough

This document is a map of the AutoModSync source tree for reviewers, contributors, and automated code-analysis tools. The code files themselves also contain an `Intent:` comment above every function.

## Trust boundaries

AutoModSync has three distinct trust boundaries:

1. **Server identity** — each server owns an RSA keypair. The server signs the exact manifest text; the client derives and pins the public-key fingerprint on first trust.
2. **Transferred content** — the client requests only missing/changed manifest entries, verifies the bundle hash/size, then verifies every extracted file against the signed manifest before staging it.
3. **Filesystem containment** — both manifest paths and ZIP entry paths are normalized and resolved beneath fixed BepInEx/AutoModSync roots. Parent traversal, drive-style paths, control characters, and unsupported target kinds are rejected.

Signing the AutoModSync release itself is separate from the server-manifest signature. Authenticode identifies the publisher of AutoModSync's own binaries; the server RSA identity authenticates what a trusted game server advertised.

## Main source files

### Source/ValheimAutoModSync.Client.cs

The in-game client plugin owns connection preflight, server trust, download verification, staging, restart/reconnect state, and the small synchronization UI.

2.5.0 connection sequence:

```text
Valheim creates outgoing ZNet connection
  -> AutoModSync OnNewConnection prefix registers AMS RPCs
  -> preflight gate is armed
  -> vanilla ZRpc.Invoke("ServerHandshake") is held
  -> OnNewConnection postfix sends AMS4_Hello
     -> no AMS response: fail open and replay ServerHandshake
     -> AMS4_Ack: wait for signed manifest
     -> manifest matches: replay ServerHandshake
     -> files differ:
          first-contact fingerprint trust
          request exact changed-file bundle
          verify package + every file
          stage files
          persist reconnect/launch context
          start apply helper
          quit Valheim
```

After a successful/matching preflight, AutoModSync stops gating and Valheim plus other mods continue their normal handshakes. Jotunn, ServerSync-style mods, and similar validators are not patched or told to ignore mismatches; they see the synchronized client after preflight.

The older SendPeerInfo AMS4 probe remains as a fallback for connection paths that do not pass through the early gate. Keeping the same AMS4 protocol preserves compatibility with existing 2.4.x AutoModSync peers.

For large first-time synchronizations, 2.5.0 also negotiates the optional `bundle-window1` capability through `AMS4_Ack`. A capable client requests up to 16 ordered bundle chunks per pull instead of one. The server opens/seeks the prepared ZIP once for each requested window and services the entire window sequentially. If the capability is absent, the client keeps the original one-chunk AMS4 behavior.

### Source/ValheimAutoModSync.Server.cs

The server plugin registers AMS4 RPCs on incoming Valheim connections and serves a deterministic signed view of eligible `BepInEx/plugins` files.

2.5.0 adds an optional `AMS4_Ack` immediately after a valid hello and before manifest hashing. New clients use that acknowledgement to distinguish a slow manifest build from a non-AutoModSync server. Older clients ignore the unknown acknowledgement and continue to understand the existing AMS4 manifest messages.

The server never opens a second listener or contacts an external download service.

### Source/ValheimAutoModSync.Installer.cs

The standalone GUI installer is the primary manual-distribution entry point for 2.5.0. It requires administrator elevation through its embedded Windows manifest because Steam installations commonly live under Program Files.

It performs no network downloads. The release builder downloads the pinned BepInEx archive once at build time, verifies its fixed SHA-256, and places that archive under `Bundled/`. The installer verifies that same SHA-256 before extracting BepInEx for a dedicated-server install.

The installer supports the same three roles as the fallback BAT file:

- Client
- Dedicated Server
- Host & Play

It preserves existing BepInEx installations, preserves existing server config/signing identity, refuses to overwrite an unknown `winhttp.dll`, and removes a legacy packed `version.dll` only when its SHA-256 matches the known historical AutoModSync bootstrap.

### Source/ValheimAutoModSync.Apply.cs

The apply helper runs outside Valheim after a verified download. Its job is intentionally narrow:

- wait for the old Valheim process to exit;
- move/replace only files listed in AutoModSync's pending staging file;
- preserve the reconnect token for the new client process;
- relaunch through the captured package-manager/Steam context when available.

This helper does not discover mods, fetch network content, decide server trust, or bypass validation.

### Source/AutoModSync.BuildTool.cs

Build/install utility functions include:

- SHA-256 calculation;
- dedicated-server path discovery;
- creation/preservation of the server RSA identity;
- a legacy packed-bootstrap writer retained for historical tooling compatibility.

Current transparent releases do not use the old packed `version.dll` bootstrap.

## Restart and reconnect flow

```text
verified bundle
  -> staging/plugins/*.amsnew
  -> pending.txt
  -> reconnect.txt
  -> optional launch-context.txt
  -> ValheimAutoModSync.Apply.exe
  -> old Valheim exits
  -> helper replaces files
  -> helper relaunches Valheim
  -> client consumes reconnect.txt
  -> FejdStartup reconnect
  -> 2.5.0 preflight runs again
  -> normal mod handshakes continue
```

## Fail-open behavior

AutoModSync does not make unrelated servers depend on AutoModSync.

If an outgoing server does not answer the preflight probe, the client releases the held vanilla `ServerHandshake` after the discovery timeout. If AutoModSync encounters a malformed acknowledgement/manifest or cannot safely process a transfer, it applies no unverified files and returns control to normal Valheim networking where it is safe to do so.

A server-side mod validator can still reject the client after that point. AutoModSync does not suppress another mod's compatibility decision.

## Release signing

The branch contains:

- `SIGNING.md`
- `sign-release.ps1`
- `build-signed-release.bat`

The intended public-release path Authenticode-signs only AutoModSync-authored PE files, verifies their signatures before packaging, and fails a signing-required release if any signature is missing/invalid. Third-party BepInEx/Doorstop binaries are never re-signed as though they were authored by AutoModSync.

## Review-surface inventory

A source review of the five C# files shows the following intentional privileged surfaces:

- **No HTTP/WebClient/HttpClient downloader exists in the C# runtime.** Plugin bytes are transferred only over Valheim's existing `ZRpc` connection. The separate build/install scripts may obtain the pinned BepInEx package and verify its fixed SHA-256.
- **Process launch:** only the client starts `ValheimAutoModSync.Apply.exe`, and the helper starts Steam/Valheim for the requested restart. The installer itself does not launch downloaded code or fetch executables.
- **Registry access:** BuildTool and the apply helper read Steam install locations; they do not write registry values.
- **Native Windows imports:** the client imports only `MessageBox`, `GetConsoleWindow`, and `ShowWindow` for first-contact trust UI and console presentation.
- **Filesystem mutation:** the client/helper write AutoModSync state/staging files and synchronized files beneath validated BepInEx plugin roots. The server writes its signing identity/cache files.
- **Cryptography:** server RSA signs manifests; client RSA verifies those signatures; SHA-256 identifies server keys, bundles, and synchronized files.

These comments/inventories are intended to make review easier, not to replace review. A reviewer should treat executable statements, path checks, and cryptographic checks as authoritative.

## Review guidance

For a security/code review, start with these functions:

- Client: `PreparePreflightGate`, `BeginPreflightProbe`, `ResumeNormalHandshake`, `RPC_ManifestEnd`, `ExtractBundleToStaging`, `EnsureServerTrusted`, `SafeUnder`.
- Server: `RPC_Hello`, `EnsureManifest`, `ResolveBundleRecord`, `RPC_GetBundle`.
- Apply helper: `Main`, `SafeUnder`, `TryLaunchSavedContext`.
- Build tool: `EnsureIdentity`, `PrintSha256`.

The comments above each function are documentation only; executable behavior remains the source of truth.
