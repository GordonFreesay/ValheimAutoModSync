# Valheim AutoModSync

<p align="center">
  <img src="Thunderstore/icon.png" alt="Valheim AutoModSync icon" width="160">
</p>

**Current public release: 2.6.0**

AutoModSync provides server-driven BepInEx mod-file synchronization for Valheim over the game's existing network connection. Players connect normally; AutoModSync compares the server's signed manifest with the client's synchronized BepInEx files, transfers only missing or changed files, verifies them, restarts Valheim when required, and reconnects.

- Website: https://gordonfreesay.com/AutoModSync
- Repository: https://github.com/gordonfreesay/ValheimAutoModSync
- Releases: https://github.com/gordonfreesay/ValheimAutoModSync/releases
- 2.6.0 release notes: [RELEASE-NOTES-2.6.0.md](RELEASE-NOTES-2.6.0.md)
- Verify official downloads: [VERIFYING-RELEASES.md](VERIFYING-RELEASES.md)

## Features

- Automatic server-to-client BepInEx plugin synchronization, including recursive plugin subfolders.
- Synchronizes required `BepInEx\patchers` files and explicitly allowlisted `BepInEx\config` files in addition to normal plugins.
- Explicit server-only/client-required wildcard classification; config synchronization remains opt-in.
- Uses Valheim's existing game connection; no separate AutoModSync sync port is required.
- Transfers only missing or changed synchronized files.
- Content-addressed bundle caching, startup prewarming, bounded concurrent scheduling, and exact-artifact interrupted-download resume for large public-server payloads.
- Signed server manifest and SHA-256 file verification.
- Durable transactional apply/recovery plus fingerprint-scoped ownership-safe stale cleanup.
- Branded in-game synchronization UI with queue state, current/average throughput, ETA, verification, restart/reconnect, and resume-retained-byte telemetry.
- Passive Steam server-browser AMS presence/badge with server/client opt-outs.
- Automatic restart after synchronized files are staged.
- Automatic reconnect to the server that triggered synchronization.
- Existing extra client plugins are not automatically deleted.
- One installer supports Client, Dedicated Server, and Host & Play roles.
- One shared semantic version is used across the standalone, Nexus Mods, CurseForge, and Thunderstore/r2modman packages.

## Installation

Close Valheim and any running Valheim Dedicated Server first. Download and extract `ValheimAutoModSync-2.6.0.zip`, then run:

```text
ValheimAutoModSyncInstaller.exe
```

The installer is the primary standalone install path. It uses the AMS-branded Client / Dedicated Server / Host & Play workflow, detects complete installs for Repair / Update, offers ownership-safe uninstall, uses only files bundled in the release, verifies the pinned BepInEx archive before server-side installation, and does not download BepInEx or mods at runtime.

`install.bat` remains included as a readable/manual fallback.

Choose one of the install modes in the installer:

1. **Client** — for players joining AutoModSync-enabled servers.
2. **Dedicated Server** — installs the server component and prepares the synchronized client payload.
3. **Host & Play** — installs both roles into the normal Valheim installation.

Launch Valheim or the dedicated server normally after installation.

## Distribution channels

AutoModSync uses one shared version across every channel. The current software version is **2.6.0** whether it is installed from the standalone GitHub release, Nexus Mods, CurseForge, or Thunderstore/r2modman.

Store-specific archive names identify packaging targets only; they do not create separate AutoModSync versions or separate GitHub releases. See `DISTRIBUTION.md` for the build/publishing workflows and required store credentials.

## What happens when a client connects

1. **Trust** — on first contact, the client is shown a short security code derived from the server signing identity and chooses whether to trust it. The complete fingerprint remains internal for cryptographic pinning.
2. **Compare** — local synchronized BepInEx file hashes are compared with the server's signed manifest.
3. **Download** — only missing or changed synchronized files are transferred.
4. **Verify** — received data and extracted files are verified before installation.
5. **Restart** — changed files are staged and Valheim restarts so BepInEx can load them.
6. **Reconnect** — AutoModSync returns to the same server when the connection type permits it. Valheim still owns any server password prompt.

## Third-party mod redistribution

AutoModSync does not grant redistribution rights for third-party mods. Before configuring a server to send a third-party mod or related file to clients, the server operator is responsible for confirming that the applicable license or author permissions allow that redistribution and for complying with any conditions.

A private, password-protected, friends-only, or noncommercial server does **not by itself** create redistribution permission. This is especially important for public servers, where synchronized files may be distributed to an unrestricted number of players. Mods that prohibit redistribution, or whose permissions are unclear, should be excluded from AutoModSync transfer unless the operator obtains appropriate permission from the rights holder.

AutoModSync itself does not bundle arbitrary third-party gameplay mods or fetch them from mod repositories on behalf of the operator. See [THIRD-PARTY-MOD-REDISTRIBUTION.md](THIRD-PARTY-MOD-REDISTRIBUTION.md) for the operator policy.

## Security model

BepInEx plugins and preloader patchers can execute code. Only approve first-contact AutoModSync trust for servers you recognize and intend to join.

Each AutoModSync server has its own signing identity. First contact shows a short human-comparison security code; the full 256-bit fingerprint remains internal for cryptographic pinning and equality checks. The server signs its synchronization manifest, and files are checked against that manifest before being applied. A server's private signing key must not be distributed to clients.

The generated server private signing identity (`BepInEx/config/ValheimAutoModSync.private.xml`) is intentionally excluded from synchronization and should never be distributed or committed.

## Repository layout

High-level layout of the checked-in source tree:

```text
Source/                         AutoModSync C# client/server/apply/installer and shared safety/state code
Server/                         Example dedicated-server configuration
Thunderstore/                   Thunderstore/r2modman metadata, README, changelog, notices, icon, and tcli config
ModSites/                       Nexus Mods / CurseForge package documentation
tests/                          Development, CI, live-runtime, installer, and release-qualification harnesses
.github/scripts/                GitHub Actions build-preparation helpers
.github/workflows/              CI, validation, release, attestation, and store-publishing workflows
THIRD_PARTY_LICENSES/           Third-party license texts

README.md                       Project overview and installation/security documentation
RELEASE-NOTES-2.6.0.md          Human-facing AutoModSync 2.6.0 release notes
TESTING-2.6.md                  Authoritative 2.6 validation evidence and release-gate record
IMPLEMENTATION-2.6.md           2.6 engineering/implementation record
SOURCE-WALKTHROUGH.md           End-to-end source, trust-boundary, handshake, apply, and reconnect map
DISTRIBUTION.md                 Versioning, artifact, attestation, and publishing policy
SIGNING.md                      Authenticode status and GitHub/Sigstore provenance model
VERIFYING-RELEASES.md           Commands for verifying official package provenance and SHA-256
THIRD-PARTY-NOTICES.md          Third-party/runtime attribution
VERSION                         Authoritative semantic version shared by every distribution target

build-*.bat / build-*.ps1       Development, standalone, branding, and store-package builders
deploy-dev.ps1                  Development deployment helper
install.bat                     Readable/manual standalone-install fallback
verify-*.ps1                    Source/version/privacy build guards
write-release-checksums.ps1     Deterministic release SHA-256 manifest generator
```

The validation harnesses intentionally live under `tests/` rather than cluttering the repository root. See `tests/README.md` for how the phase, installer, and release gates are grouped.

Generated runtime payloads such as `Client/`, `Tools/`, the compiled server DLL, installers, `DevBuild/`, and `Dist/` are build outputs and are intentionally not versioned. Installable binaries are published through GitHub Releases or the corresponding mod-distribution channel rather than stored in the source tree.

There are intentionally no nested `README.txt` files; the Markdown documents above are the maintained project documentation.

## Building from source

Local builds can run `build-release.bat` on a Windows PC with Valheim installed. The builder uses the Windows .NET Framework C# compiler to build the managed AutoModSync components and produces the runtime files used by the release package. These generated payloads are ignored by Git; a source checkout is not itself an install package.

The root `VERSION` file is the authoritative distribution version. `verify-version.ps1` checks the client/server plugin versions and all AutoModSync assembly/installer versions before a release build. `build-all-releases.bat` builds the standalone, Nexus, CurseForge, and Thunderstore packages from the same compiled binaries.

The repository also contains `.github/workflows/release-build.yml`. That workflow builds on a GitHub-hosted Windows runner, obtains the freely downloadable Valheim Dedicated Server through SteamCMD for compile-time game references, builds the release from the checked-out source, generates SHA-256 checksums, and creates GitHub/Sigstore build-provenance attestations for official GitHub-built artifacts. The Windows PE files remain Authenticode-unsigned unless trusted signing credentials are configured. Optional SignPath submission remains dormant unless such credentials become available.

The release builder pins **BepInExPack Valheim 5.4.2350** and verifies this SHA-256 before using it:

```text
37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f
```

Valheim and Unity assemblies required for compilation are taken from the user's local Valheim installation and are not redistributed as source dependencies in this repository.

## Code signing policy

AutoModSync public Windows PE files are currently **not Authenticode-signed by a publicly trusted publisher certificate**.

The project applied to the SignPath Foundation program in September 2026. The application was declined at this stage because the project did not yet have enough external public-trust and adoption signals such as community usage, stars/forks/contributors, independent references, or sustained public engagement. The decision was not a technical rejection of AutoModSync. The project may reapply after broader adoption or use another trusted Authenticode signing path in the future.

Release builds continue to run through GitHub Actions on GitHub-hosted runners. Starting with the 2.6 release pipeline, official GitHub-built package bytes receive SHA-256 manifests and GitHub/Sigstore artifact attestations that bind the digest to this repository, workflow, commit, and build event. This provenance is separate from Authenticode and does not suppress Windows SmartScreen.

The workflow retains optional SignPath support but does not submit signing requests unless valid SignPath credentials are explicitly configured.

Only AutoModSync-authored PE files would receive an AutoModSync project signature if trusted signing is enabled in the future. Third-party BepInEx and Unity Doorstop binaries included in release packages are not re-signed.

Project roles:

- Authors / committers: GordonFreesay
- Reviewers: GordonFreesay; external contributions are reviewed before they are merged
- Signing approver: GordonFreesay

Privacy policy: This program will not transfer information to other networked systems unless specifically requested by the user or by the person installing or operating it. AutoModSync communicates with the Valheim server the user chooses to connect to for synchronization and uses the game's existing network connection.

See `SIGNING.md` for the Authenticode/provenance distinction and `VERIFYING-RELEASES.md` for verification commands.

## Release integrity

The current public standalone release is `ValheimAutoModSync-2.6.0.zip`.

GitHub Releases is the authoritative source for the standalone installer artifact. The release includes `SHA256SUMS.txt` rather than hard-coding a digest in source documentation. Nexus Mods, CurseForge, and Thunderstore use store-specific packaging variants of the same AutoModSync version. Those variants are not separate GitHub releases. See `DISTRIBUTION.md`.

For official GitHub-built packages, use:

```powershell
gh attestation verify .\ValheimAutoModSync-2.6.0.zip -R GordonFreesay/ValheimAutoModSync
```

The canonical checksum manifest is also attested. See `VERIFYING-RELEASES.md` for exact standalone/store commands, SHA-256 verification, and the distinction between GitHub/Sigstore provenance and Windows Authenticode.

## License

AutoModSync-authored source is released under the **MIT License**. See `LICENSE`.

The client runtime includes third-party BepInEx/Unity Doorstop components as normal visible files. Those components remain under their respective upstream licenses; see `THIRD-PARTY-NOTICES.md` and `THIRD_PARTY_LICENSES/`.

The MIT license covers AutoModSync-authored code only. It does not license or grant redistribution rights for third-party mods selected by a server operator for synchronization. See `THIRD-PARTY-MOD-REDISTRIBUTION.md`.

## Release notes (2.4.5+)

### 2.6.0

- Adds content-addressed bundle caching, startup prewarming, and single-flight package construction so identical fresh-client requests reuse one immutable verified ZIP instead of recompressing the same payload per client.
- Adds a bounded FIFO transfer scheduler with configurable active/queued limits, round-robin aggregate bandwidth grants, Steam reliable-queue backpressure, persistent per-transfer streams, and queue-position UI.
- Adds exact-artifact interrupted-download resume. Clients retain one bounded verified prefix; the server independently hashes that exact prefix before accepting a nonzero resume point.
- Adds server-owned `BepInEx/AutoModSync/ClientPayload/plugins/**` for client-required files the dedicated server itself must not load.
- Adds fingerprint-scoped ownership ledgers and ownership-safe stale removal. AutoModSync removes only exact bytes it previously installed for the same trusted server; locally modified, unrelated, and cross-server files are preserved.
- Replaces destructive apply with a durable PREPARED/COMMITTED transaction and verified backup/rollback recovery for interrupted updates.
- Adds explicit client/server resource ceilings, strict fixed-root path validation, Windows alias/device-name defenses, reparse-point rejection, bounded ZIP extraction, and recognized-AMS fail-closed handling while preserving fail-open behavior for non-AMS servers.
- Adds the branded in-game synchronization panel with comparison counts, queue state, current/average throughput, ETA, retained resume bytes, verification/apply/restart/reconnect state, and bounded failure presentation.
- Replaces normal full-fingerprint display with a short first-contact security code while retaining the complete fingerprint internally for signature trust/pinning.
- Adds passive Steam server-browser AMS presence and a client-side AMS badge using exact Valheim row ownership so pooled/reused rows do not leak badges to unrelated servers. Both advertisement and display can be disabled.
- Polishes the standalone installer with AMS branding, live complete/partial install detection, Repair / Update, and role-aware uninstall. Shared BepInEx and unrelated files are preserved; server identity/config are preserved unless explicitly selected for removal.
- Adds first-party PII scanning to development/release builds and CI.
- Adds SHA-256 release manifests and GitHub/Sigstore artifact attestations for official GitHub-built standalone and store packages. This provenance is verifiable with GitHub CLI and is separate from Authenticode/SmartScreen trust.
- Keeps AMS4 / protocol 4 and retains compatibility fallbacks for older AMS4 peers.

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
