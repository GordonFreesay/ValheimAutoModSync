# Distribution policy

AutoModSync uses **one semantic version across every distribution channel**.

The authoritative version is the root `VERSION` file. Build scripts and GitHub Actions read that value. `verify-version.ps1` fails the build if the AutoModSync plugin/assembly metadata does not match it.

For the current release, every channel is **2.5.0**. A store name in an archive filename identifies the packaging target; it is not a different software version.

## Artifacts

| Channel | Version shown to users | Artifact | Purpose |
| --- | --- | --- | --- |
| GitHub / website | `2.5.0` | `ValheimAutoModSync-2.5.0.zip` | Canonical standalone installer package with bundled, hash-pinned BepInEx |
| Nexus Mods | `2.5.0` | `ValheimAutoModSync-2.5.0-Nexus.zip` | Lightweight Nexus package; BepInEx is a separate requirement and no archive is nested inside the ZIP |
| CurseForge | `2.5.0` | `ValheimAutoModSync-2.5.0-CurseForge.zip` | Lightweight CurseForge package; BepInEx is a separate requirement |
| Thunderstore / r2modman | `2.5.0` | `GordonFreesay-ValheimAutoModSync-2.5.0.zip` | Native Thunderstore package with `manifest.json` and BepInEx dependency metadata |

Store-specific packages are **not separate GitHub releases**. The GitHub `v2.5.0` release remains the canonical standalone release, and the website should continue to say that the current AutoModSync version is **2.5.0**.

GitHub Actions artifacts are used as staging outputs for the store packages. After publication, the Nexus/CurseForge/Thunderstore pages are the normal download locations for those variants.

## Local build commands

Build every distribution format from the same compiled binaries:

    build-all-releases.bat

Individual targets:

    powershell -ExecutionPolicy Bypass -File .\build-thunderstore.ps1
    powershell -ExecutionPolicy Bypass -File .\build-nexus.ps1
    powershell -ExecutionPolicy Bypass -File .\build-curseforge.ps1

The store-specific builders intentionally do not bundle BepInEx. Only the standalone GitHub package includes the pinned BepInEx archive.

## GitHub Actions

### Distribution Packages

`.github/workflows/distribution-packages.yml`

Runs manually or when a `v*` tag is pushed. It builds the standalone, Nexus, CurseForge, and Thunderstore ZIPs from the same source revision and uploads each as a GitHub Actions artifact.

### Nexus Mods

`.github/workflows/publish-nexus.yml`

The workflow always builds and preserves the Nexus ZIP. Publishing is manual and additionally gated because Nexus Mods' file-submission rules require staff contact when an executable/tool's crucial functionality depends on sending or receiving files over the network.

Before enabling automated publication:

1. Contact Nexus Mods staff and disclose AutoModSync's server-to-client synchronization behavior and public source repository.
2. Create the Nexus mod page and perform the initial file submission as required by Nexus.
3. After staff clearance, configure:
   - secret: `NEXUSMODS_API_KEY`
   - variable: `NEXUSMODS_MOD_ID`
   - variable: `NEXUSMODS_FILE_ID`
   - variable: `NEXUS_NETWORK_TOOL_CLEARANCE=approved`
4. Run **Publish Nexus** manually with `publish=true`.

The Nexus package contains no nested ZIP/7z/RAR/tar archive and does not bundle BepInEx.

### CurseForge

`.github/workflows/publish-curseforge.yml`

Configure:

- secret: `CURSEFORGE_API_TOKEN`
- variable: `CURSEFORGE_PROJECT_ID`
- optional variable: `CURSEFORGE_BEPINEX_PROJECT_ID` if a suitable BepInEx project exists on CurseForge and should be recorded as a required dependency

Run the workflow manually. With `publish=true`, it uploads through CurseForge's project upload API as a `release`, marks the file for **manual release**, and labels it for Client/Server compatibility. Manual release staging means moderation/upload can complete without automatically making the file public before you review it.

### Thunderstore

`.github/workflows/publish-thunderstore.yml`

Configure:

- secret: `THUNDERSTORE_TOKEN`

The token should belong to a Thunderstore team service account allowed to publish the `GordonFreesay` namespace. The workflow uses Thunderstore's official `tcli` tool and the checked-in `Thunderstore/thunderstore.toml`.

Run the workflow manually with `publish=true` to publish. With `publish=false`, it only builds and uploads the package as a GitHub Actions artifact.

## Release procedure

For a future release:

1. Update the root `VERSION`.
2. Update the matching AutoModSync source/assembly version declarations.
3. Update changelog/release notes.
4. Run the build. `verify-version.ps1` prevents version drift.
5. Publish the canonical GitHub standalone release/tag.
6. Run the distribution/store workflows for that same version.
7. Keep the website's current version equal to the shared AutoModSync version; link to store pages as alternate installation channels rather than presenting them as different versions.

## Signing

Public Windows releases are currently unsigned. Store packaging does not change that status. See `SIGNING.md`.
