# Distribution policy

AutoModSync uses **one semantic version across every distribution channel**.

The authoritative version is the root `VERSION` file. Build scripts and GitHub Actions read that value. `verify-version.ps1` fails the build if the AutoModSync plugin/assembly metadata does not match it.

For the current release, every channel is **2.6.0**. A store name in an archive filename identifies the packaging target; it is not a different software version.

## Artifacts

| Channel | Version shown to users | Artifact | Purpose |
| --- | --- | --- | --- |
| GitHub / website | `2.6.0` | `ValheimAutoModSync-2.6.0.zip` | Canonical standalone installer package with bundled, hash-pinned BepInEx |
| Nexus Mods | `2.6.0` | `ValheimAutoModSync-2.6.0-Nexus.zip` | Lightweight Nexus package; BepInEx is a separate requirement and no archive is nested inside the ZIP |
| CurseForge | `2.6.0` | `ValheimAutoModSync-2.6.0-CurseForge.zip` | Lightweight CurseForge package; BepInEx is a separate requirement |
| Thunderstore / r2modman | `2.6.0` | `GordonFreesay-ValheimAutoModSync-2.6.0.zip` | Native Thunderstore package with `manifest.json` and BepInEx dependency metadata |

Store-specific packages are **not separate GitHub releases**. The GitHub `v2.6.0` release is the canonical standalone release, and the website should identify **2.6.0** as the current AutoModSync version.

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

Runs manually or when a `v*` tag is pushed. It builds the standalone, Nexus, CurseForge, and Thunderstore ZIPs from the same source revision, generates one `SHA256SUMS.txt` covering all four package files, creates GitHub/Sigstore provenance attestations for those exact digests plus the checksum manifest, and uploads each package/checksum file as a GitHub Actions artifact.

### Nexus Mods

`.github/workflows/publish-nexus.yml`

The workflow always builds and preserves the Nexus ZIP. Publishing is manual and additionally gated because Nexus Mods' file-submission rules require staff contact when an executable/tool's crucial functionality depends on sending or receiving files over the network.

Before enabling automated publication:

1. Contact Nexus Mods staff and disclose AutoModSync's server-to-client synchronization behavior and public source repository.
2. Create the Nexus mod page and perform the initial file submission as required by Nexus.
3. After staff clearance, configure:
   - secret: `NEXUSMODS_API_KEY`
   - variable: `NEXUS_NETWORK_TOOL_CLEARANCE=approved`
4. Run **Publish Nexus** manually with `publish=true`.

The workflow knows the public AutoModSync Nexus page is Valheim mod `4006` and pins its persistent Nexus API File ID as `8027793`. At publish time it still validates that this file ID belongs to the mod before creating a new file version.

The official Nexus upload action creates a new version of an existing file slot. The current AutoModSync Nexus page is a fresh 2.6-era page, so it needs one initial file submission before this workflow can update that slot automatically. After that first file exists, normal releases can resolve and update the active file slot through the API.

The Nexus package contains no nested ZIP/7z/RAR/tar archive and does not bundle BepInEx.

### CurseForge

`.github/workflows/publish-curseforge.yml`

Configure:

- secret: `CURSEFORGE_API_TOKEN`
- optional variable: `CURSEFORGE_BEPINEX_PROJECT_ID` if a suitable BepInEx project exists on CurseForge and should be recorded as a required dependency

The AutoModSync CurseForge project ID (`1702780`) is pinned in the project-specific publishing workflow, so it does not need to be duplicated as a repository variable.

Run the workflow manually. With `publish=true`, it uploads through CurseForge's project upload API as a `release`, marks the file for **manual release**, and labels it for Client/Server compatibility. Manual release staging means moderation/upload can complete without automatically making the file public before you review it.

### Thunderstore

`.github/workflows/publish-thunderstore.yml`

Configure:

- secret: `THUNDERSTORE_TOKEN`

The token should belong to a Thunderstore team service account allowed to publish the `GordonFreesay` namespace. The workflow uses Thunderstore's official `tcli` tool and the checked-in `Thunderstore/thunderstore.toml`.

Run the workflow manually with `publish=true` to publish. With `publish=false`, it only builds and uploads the package as a GitHub Actions artifact.

Each manual store-publishing workflow also SHA-256 hashes and attests the exact store ZIP produced in that publishing run before upload. Do not substitute an independently rebuilt ZIP merely because it has the same version number; provenance is digest-specific.

## Release procedure

For a release:

1. Freeze the release branch: update `VERSION`, source/assembly metadata, changelog, README/store copy, and release notes; stop runtime feature changes.
2. Run `tests/test-release-readiness.ps1` and require the Release Readiness, privacy, installer, branding, and provenance CI gates to pass.
3. Merge the reviewed release PR into `main` without changing release content after the validated head revision.
4. Require the `main` Standalone Release Build/readiness workflows to pass.
5. Tag the exact validated `main` commit as `v<version>`. The tag triggers **Distribution Packages**.
6. Require Distribution Packages to build all four package variants, `SHA256SUMS.txt`, and GitHub/Sigstore attestations successfully.
7. Download the workflow-produced canonical standalone ZIP and `SHA256SUMS.txt`; run `gh attestation verify <artifact> -R GordonFreesay/ValheimAutoModSync` against the exact downloaded bytes, and verify the checksum manifest too.
8. Only after verification succeeds, publish the canonical GitHub Release using those exact workflow-produced bytes plus `SHA256SUMS.txt` and the checked-in release notes.
9. Run the Nexus/CurseForge/Thunderstore publication workflows for the same version. Each workflow independently hashes and attests the exact store ZIP it uploads.
10. Update the website to the same shared AutoModSync version and link the store pages as alternate installation channels rather than separate versions.

Do not rebuild/repackage an artifact between attestation verification and publication. Provenance is tied to the exact digest.

## Signing

AutoModSync PE files are currently Authenticode-unsigned, but official GitHub-built 2.6+ packages receive SHA-256 manifests and GitHub/Sigstore provenance attestations. Store packaging does not convert provenance into Authenticode. See `SIGNING.md` and `VERIFYING-RELEASES.md`.
