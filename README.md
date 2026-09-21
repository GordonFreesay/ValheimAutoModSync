# Valheim AutoModSync

**Current public release: 2.5.0**

AutoModSync provides server-driven BepInEx mod-file synchronization for Valheim over the game's existing network connection. Players connect normally; AutoModSync compares the server's signed manifest with the client's synchronized BepInEx files, transfers only missing or changed files, verifies them, restarts Valheim when required, and reconnects.

- Website: https://gordonfreesay.com/AutoModSync
- Repository: https://github.com/gordonfreesay/ValheimAutoModSync
- Releases: https://github.com/gordonfreesay/ValheimAutoModSync/releases

## Features

- Automatic server-to-client BepInEx plugin synchronization, including recursive plugin subfolders.
- 2.5.0 support for required `BepInEx\patchers` files and explicitly allowlisted `BepInEx\config` files.
- Explicit server-only/client-required wildcard classification; config synchronization remains opt-in.
- Uses Valheim's existing game connection; no separate AutoModSync sync port is required.
- Transfers only missing or changed synchronized files.
- Signed server manifest and SHA-256 file verification.
- In-game synchronization/download progress.
- Automatic restart after synchronized files are staged.
- Automatic reconnect to the server that triggered synchronization.
- Existing extra client plugins are not automatically deleted.
- One installer supports Client, Dedicated Server, and Host & Play roles.
- The same runtime source also supports native Thunderstore/r2modman packaging.

## Installation

Close Valheim and any running Valheim Dedicated Server first. Download and extract `ValheimAutoModSync-2.5.0.zip`, then run:

```text
ValheimAutoModSyncInstaller.exe
```

The installer is now the primary standalone install path. It uses only files bundled in the release, verifies the pinned BepInEx archive before server-side installation, and does not download BepInEx or mods at runtime.

`install.bat` remains included as a readable/manual fallback.

Choose one of the install modes in the installer:

1. **Client** — for players joining AutoModSync-enabled servers.
2. **Dedicated Server** — installs the server component and prepares the synchronized client payload.
3. **Host & Play** — installs both roles into the normal Valheim installation.

Launch Valheim or the dedicated server normally after installation.

## What happens when a client connects

1. **Trust** — on first contact, the client is shown the server signing fingerprint and chooses whether to trust it.
2. **Compare** — local synchronized BepInEx file hashes are compared with the server's signed manifest.
3. **Download** — only missing or changed synchronized files are transferred.
4. **Verify** — received data and extracted files are verified before installation.
5. **Restart** — changed files are staged and Valheim restarts so BepInEx can load them.
6. **Reconnect** — AutoModSync returns to the same server when the connection type permits it. Valheim still owns any server password prompt.

## Security model

BepInEx plugins and preloader patchers can execute code. Only trust AutoModSync server fingerprints belonging to server operators you recognize and trust.

Each AutoModSync server has its own signing identity. The server signs its synchronization manifest, and files are checked against that manifest before being applied. A server's private signing key must not be distributed to clients.

The generated server private signing identity (`BepInEx/config/ValheimAutoModSync.private.xml`) is intentionally excluded from synchronization and should never be distributed or committed.

## Repository layout

```text
Client/                  Normal visible BepInEx client runtime, AutoModSync plugin, and apply helper
Server/                  Prebuilt server plugin and example configuration
Source/                  AutoModSync source, including client, server, installer, build tool, and apply helper
Tools/                   Prebuilt release build tool
build-release.bat        Windows release builder
ValheimAutoModSyncInstaller.exe  Primary standalone installer in built release packages
install.bat              Readable/manual fallback installer
CHECKSUMS.txt             SHA-256 hashes for repository/package files
LICENSE                   MIT license for AutoModSync-authored code
THIRD-PARTY-NOTICES.md    Bundled dependency attribution and license information
SOURCE-WALKTHROUGH.md      End-to-end source, trust-boundary, handshake, and restart flow map
SIGNING.md                 Authenticode/public-release signing workflow
verify-source-docs.ps1      Checks that every C# function retains an Intent comment
```

There are intentionally no nested `README.txt` files; this root `README.md` is the project documentation.

## Building from source

Local builds can run `build-release.bat` on a Windows PC with Valheim installed. The builder uses the Windows .NET Framework C# compiler to build the managed AutoModSync components and produces the runtime files used by the release package.

The repository also contains `.github/workflows/signpath-release.yml`. That workflow builds on a GitHub-hosted Windows runner, obtains the freely downloadable Valheim Dedicated Server through SteamCMD for compile-time game references, builds the release from the checked-out source, and uploads the resulting ZIP as a GitHub Actions artifact. The uploaded workflow artifact is the artifact submitted to SignPath for origin-verified signing once the SignPath Foundation project credentials are configured.

The release builder pins **BepInExPack Valheim 5.4.2350** and verifies this SHA-256 before using it:

```text
37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f
```

Valheim and Unity assemblies required for compilation are taken from the user's local Valheim installation and are not redistributed as source dependencies in this repository.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

Public release signing is performed from the `main` branch through the repository's GitHub Actions workflow on GitHub-hosted runners. The unsigned release ZIP is uploaded as a GitHub Actions artifact before any signing request is submitted so SignPath can verify the repository, branch, commit, workflow, and build origin.

Only AutoModSync-authored PE files are intended to receive the AutoModSync project signature. Third-party BepInEx and Unity Doorstop binaries included in release packages are not re-signed.

Project roles:

- Authors / committers: GordonFreesay
- Reviewers: GordonFreesay; external contributions are reviewed before they are merged
- Signing approver: GordonFreesay

Privacy policy: This program will not transfer information to other networked systems unless specifically requested by the user or by the person installing or operating it. AutoModSync communicates with the Valheim server the user chooses to connect to for synchronization and uses the game's existing network connection.

See `SIGNING.md` for the release-signing workflow and configuration details.

## Checksums

`CHECKSUMS.txt` contains SHA-256 hashes for the files shipped with the package/repository. The checksum file itself is intentionally excluded from its own list.

## License

AutoModSync-authored source is released under the **MIT License**. See `LICENSE`.

The client runtime includes third-party BepInEx/Unity Doorstop components as normal visible files. Those components remain under their respective upstream licenses; see `THIRD-PARTY-NOTICES.md` and `THIRD_PARTY_LICENSES/`.

## Release notes (2.4.5+)

### 2.5.0

- Moves AutoModSync preflight ahead of the normal Valheim `ServerHandshake` so Jotunn/Epic Loot and similar mod-compatibility checks run only after synchronization has had a chance to complete.
- Holds/replays only Valheim's own `ServerHandshake`; third-party compatibility results are not bypassed or rewritten.
- Preserves and replays the exact original `ServerHandshake` argument list instead of synthesizing an empty call, preventing malformed-handshake `EndOfStreamException` failures on current Valheim builds.
- Adds an optional AMS4 preflight acknowledgement before potentially expensive manifest generation.
- Speeds up first-time/bare-client synchronization with backward-compatible windowed, binary-batch, and pipelined bundle transfer modes while retaining AMS4 fallbacks for older peers.
- Tunes the live SteamNetworkingSockets connection only during AutoModSync bundle delivery, using an 8 MiB/s temporary minimum, 32 MiB/s maximum, and 16 MiB reliable buffer by default, then restores the exact previous values.
- Retries the early `AMS4_Hello` inside the fail-open preflight window to cover startup races before the server has finished registering handlers.
- Extends synchronization beyond `BepInEx/plugins`: required `BepInEx/patchers` files can now be synchronized to the patcher root, and selected `BepInEx/config` files can be synchronized through an explicit server allowlist.
- Adds `ServerOnlyPatterns` and optional `ClientRequiredPatterns` compatibility classification so dedicated-server-only files do not have to be advertised to clients.
- Keeps config synchronization opt-in and hard-blocks the AutoModSync signing identity files and loader-wide `BepInEx.cfg` from synchronized config manifests.
- Keeps protocol version 4 / AMS4 for backward compatibility with 2.4.x peers.
- Adds the Windows GUI standalone installer, Authenticode signing/verification tooling for AutoModSync-authored binaries, and source-level intent/workflow documentation.

### 2.4.8

- Fixes Thunderstore/r2modman profile state-root handling during synchronization.
- Saves the original package-manager launch context before restart.
- Relaunches Valheim through Steam with the original Doorstop/BepInEx profile arguments so the replacement process remains modded.
- Restores automatic one-shot reconnect to the server after synchronized files are applied.
- Automatically continues through the previously selected character during reconnect.
- Prevents duplicate reconnect dispatches and false reconnect timeout warnings.
- Standalone/manual installation behavior and protocol version 4 remain unchanged.

### 2.4.7

- Adds native Thunderstore/r2modman installation from the same source used by the standalone release.
- The client role disables itself on dedicated-server processes.
- The apply helper supports package-manager installation paths while keeping persistent state under `BepInEx/AutoModSync`.
- Server signing identity is generated automatically on first launch when missing.
- Package-managed clients ignore AutoModSync-owned files advertised by older standalone servers, preventing downgrade/duplicate copies.
- Package-managed servers do not advertise stale standalone release-client payloads.
- Signed manifests, SHA-256 verification, delta synchronization, restart/reconnect, and protocol version 4 are preserved.

### 2.4.6

No public 2.4.6 release is present in this repository's history; public development moved from 2.4.5 to 2.4.7.

### 2.4.5

- Replaces the packed `version.dll` bootstrap with a transparent on-disk BepInEx layout.
- Installs the AutoModSync client as a normal `BepInEx\plugins` DLL plus a visible apply helper.
- Removes the encoded-PowerShell client detection path from the public installer.
- Migrates the known 2.4.4 packed bootstrap by SHA-256 without deleting unknown `version.dll` files.
- Keeps signed manifests, delta synchronization, restart/reconnect, and file verification.

## Disclaimer

Valheim AutoModSync is an independent community project and is not affiliated with or endorsed by Iron Gate AB or Coffee Stain Publishing.
