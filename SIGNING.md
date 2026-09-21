# AutoModSync release signing

AutoModSync public Windows releases are prepared for origin-verified Authenticode signing through SignPath Foundation.

## Public release path: GitHub Actions + SignPath

The repository workflow is:

`.github/workflows/signpath-release.yml`

It runs on a GitHub-hosted Windows runner and:

1. checks out the exact source revision;
2. installs the freely downloadable Valheim Dedicated Server through SteamCMD for compile-time game references;
3. runs `build-release.bat` to build the AutoModSync release from source;
4. uploads the unsigned release ZIP as a GitHub Actions artifact;
5. on a manually started release-signing run, submits that GitHub artifact ID to SignPath;
6. waits for SignPath approval/signing and makes the signed result available as a workflow artifact.

This structure is intentional: SignPath's GitHub trusted-build connector verifies that the build came from a GitHub workflow and that the artifact existed as a GitHub Actions artifact before the signing request was submitted.

After the SignPath Foundation application is approved, configure:

### GitHub Actions secret

- `SIGNPATH_API_TOKEN`

### GitHub Actions repository variables

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY_SLUG`

Public signing requests are intentionally made only from manually dispatched workflow runs. Normal pushes to `main` still build and upload the unsigned artifact so the GitHub-hosted build remains continuously verifiable.

## Files that receive the project signature

Only AutoModSync-authored PE files should be signed:

- `ValheimAutoModSync.Client.dll`
- `ValheimAutoModSync.Server.dll`
- `ValheimAutoModSync.Apply.exe`
- `AutoModSync.BuildTool.exe`
- `ValheimAutoModSyncInstaller.exe`

Third-party BepInEx / Unity Doorstop files, including `winhttp.dll`, must not be re-signed with the AutoModSync project certificate.

The SignPath artifact configuration should use the release ZIP as its root and apply Authenticode signing only to the AutoModSync-authored files listed above.

## Local development signing

The repository retains `sign-release.ps1` and `build-signed-release.bat` for local development/testing with a developer-controlled certificate or Microsoft Artifact Signing identity.

Those local paths are not the SignPath Foundation public-release path and do not provide SignPath origin verification.

Never commit signing credentials, private keys, PFX files, API tokens, or signing metadata.
