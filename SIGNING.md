# AutoModSync release signing

## Current status

AutoModSync public Windows releases are currently **unsigned**.

The project applied to the SignPath Foundation program in September 2026. SignPath Foundation declined the application at this stage because the project did not yet show enough external public-trust and adoption signals, such as community usage, GitHub stars/forks/contributors, independent references or discussions, institutional backing, and sustained public engagement.

That decision was about the Foundation program's public-visibility threshold, not a technical rejection of AutoModSync or its build/signing design. The project may reapply after broader public adoption. A regular paid SignPath subscription or another trusted Authenticode provider could also be used independently of the Foundation program.

Unsigned releases remain the supported public release path unless and until trusted Authenticode signing is enabled.

## Current public release path: GitHub Actions unsigned build

The repository workflow is:

`.github/workflows/release-build.yml`

Its normal public-release function is to build and preserve an origin-verifiable unsigned artifact. It runs on a GitHub-hosted Windows runner and:

1. checks out the exact source revision;
2. installs the freely downloadable Valheim Dedicated Server through SteamCMD for compile-time game references;
3. runs the unified release/package build so the standalone and store packages are validated from the same source and compiled binaries;
4. uploads the unsigned release ZIP as a GitHub Actions artifact.

Public GitHub Releases may be created from that verified unsigned artifact. The release ZIP SHA-256 should be published with the release.

## Retained optional SignPath integration

The workflow still contains conditional SignPath submission steps so the integration does not have to be rebuilt if trusted SignPath signing becomes available later.

Those steps run only on a manually dispatched workflow and only when all required SignPath credentials are configured:

### GitHub Actions secret

- `SIGNPATH_API_TOKEN`

### GitHub Actions repository variables

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY_SLUG`

Without those credentials, the workflow builds and uploads the unsigned artifact only.

If SignPath signing is enabled in the future, the GitHub Actions artifact should remain the input to the signing request so the build origin can be tied to the repository, branch, commit, workflow, and artifact.

## Files eligible for a future project signature

Only AutoModSync-authored PE files should be signed:

- `ValheimAutoModSync.Client.dll`
- `ValheimAutoModSync.Server.dll`
- `ValheimAutoModSync.Apply.exe`
- `AutoModSync.BuildTool.exe`
- `ValheimAutoModSyncInstaller.exe`

Third-party BepInEx / Unity Doorstop files, including `winhttp.dll`, must not be re-signed with an AutoModSync project certificate.

## Local development signing

The repository retains `sign-release.ps1` and `build-signed-release.bat` for local development/testing with a developer-controlled certificate, Microsoft Artifact Signing identity, or another compatible trusted signing identity.

Local/self-controlled signing is not presented as SignPath Foundation signing and does not create SignPath Foundation origin verification.

Never commit signing credentials, private keys, PFX files, API tokens, or signing metadata.
