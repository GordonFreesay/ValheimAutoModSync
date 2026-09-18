# Valheim AutoModSync

**Current release: 2.4.5**

AutoModSync provides server-driven BepInEx plugin synchronization for Valheim over the game's existing network connection. Players connect normally; AutoModSync compares the server's signed manifest with the client's installed plugins, transfers only missing or changed files, verifies them, restarts Valheim when required, and reconnects.

- Website: https://gordonfreesay.com/AutoModSync
- Repository: https://github.com/gordonfreesay/ValheimAutoModSync
- Releases: https://github.com/gordonfreesay/ValheimAutoModSync/releases

## Features

- Automatic server-to-client BepInEx plugin synchronization.
- Uses Valheim's existing game connection; no separate AutoModSync sync port is required.
- Transfers only missing or changed synchronized files.
- Signed server manifest and SHA-256 file verification.
- In-game synchronization/download progress.
- Automatic restart after synchronized files are staged.
- Automatic reconnect to the server that triggered synchronization.
- Existing extra client plugins are not automatically deleted.
- One installer supports Client, Dedicated Server, and Host & Play roles.

## Installation

Close Valheim and any running Valheim Dedicated Server first. Download and extract `ValheimAutoModSync-2.4.5.zip`, then run:

```text
install.bat
```

Choose one of the install modes when prompted:

1. **Client** — for players joining AutoModSync-enabled servers.
2. **Dedicated Server** — installs the server component and prepares the synchronized client payload.
3. **Host & Play** — installs both roles into the normal Valheim installation.

Launch Valheim or the dedicated server normally after installation.

## What happens when a client connects

1. **Trust** — on first contact, the client is shown the server signing fingerprint and chooses whether to trust it.
2. **Compare** — local plugin hashes are compared with the server's signed manifest.
3. **Download** — only missing or changed synchronized files are transferred.
4. **Verify** — received data and extracted files are verified before installation.
5. **Restart** — changed files are staged and Valheim restarts so BepInEx can load them.
6. **Reconnect** — AutoModSync returns to the same server when the connection type permits it. Valheim still owns any server password prompt.

## Security model

BepInEx plugins are executable .NET code. Only trust AutoModSync server fingerprints belonging to server operators you recognize and trust.

Each AutoModSync server has its own signing identity. The server signs its synchronization manifest, and files are checked against that manifest before being applied. A server's private signing key must not be distributed to clients.

The generated server signing key (`ValheimAutoModSync.key`) is intentionally excluded by `.gitignore` and should never be committed.

## Repository layout

```text
Client/                  Normal visible BepInEx client runtime, AutoModSync plugin, and apply helper
Server/                  Prebuilt server plugin and example configuration
Source/                  AutoModSync source, including client, server, build tool, and apply helper
Tools/                   Prebuilt release build tool
build-release.bat        Windows release builder
Detect-Valheim.ps1       Valheim install detection used by the builder
install.bat              End-user installer
CHECKSUMS.txt             SHA-256 hashes for repository/package files
LICENSE                   MIT license for AutoModSync-authored code
THIRD-PARTY-NOTICES.md    Bundled dependency attribution and license information
```

There are intentionally no nested `README.txt` files; this root `README.md` is the project documentation.

## Building from source

Run `build-release.bat` on a Windows PC with the normal Valheim client installed. The builder uses the Windows .NET Framework C# compiler to build the managed AutoModSync components and produces the runtime files used by the public package.

The release builder pins **BepInExPack Valheim 5.4.2350** and verifies this SHA-256 before using it:

```text
37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f
```

Valheim and Unity assemblies required for compilation are taken from the user's local Valheim installation and are not redistributed as source dependencies in this repository.

## Checksums

`CHECKSUMS.txt` contains SHA-256 hashes for the files shipped with the package/repository. The checksum file itself is intentionally excluded from its own list.

## License

AutoModSync-authored source is released under the **MIT License**. See `LICENSE`.

The client runtime includes third-party BepInEx/Unity Doorstop components as normal visible files. Those components remain under their respective upstream licenses; see `THIRD-PARTY-NOTICES.md` and `THIRD_PARTY_LICENSES/`.

## 2.4.5 highlights

- Replaces the packed `version.dll` bootstrap with a transparent on-disk BepInEx layout.
- Installs the AutoModSync client as a normal `BepInEx\plugins` DLL plus a visible apply helper.
- Removes the encoded-PowerShell client detection path from the public installer.
- Migrates the known 2.4.4 packed bootstrap by SHA-256 without deleting unknown `version.dll` files.
- Keeps signed manifests, delta synchronization, restart/reconnect, and file verification.

## Disclaimer

Valheim AutoModSync is an independent community project and is not affiliated with or endorsed by Iron Gate AB or Coffee Stain Publishing.
