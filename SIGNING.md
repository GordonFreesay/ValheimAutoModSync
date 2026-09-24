# AutoModSync release signing

## Current status

AutoModSync public Windows binaries are currently **not Authenticode-signed by a publicly trusted publisher certificate**. Windows may therefore still display an unknown-publisher/SmartScreen warning.

Starting with the 2.6 release pipeline, official GitHub-built AutoModSync packages use a separate cryptographic provenance layer:

- SHA-256 package manifests are generated as `SHA256SUMS.txt`;
- GitHub Actions creates signed build-provenance attestations with `actions/attest@v4`;
- public-repository attestations are signed through Sigstore using the short-lived GitHub Actions OIDC build identity;
- the attestation binds the artifact digest to this repository, workflow, commit, and triggering event;
- users can verify a downloaded artifact directly with GitHub CLI without installing a custom certificate.

This provenance does **not** make the PE files Authenticode-signed and does not claim to suppress Windows SmartScreen. It provides independently verifiable build origin and integrity while trusted Authenticode remains unavailable.

The project applied to the SignPath Foundation program in September 2026. SignPath Foundation declined the application at this stage because the project did not yet show enough external public-trust and adoption signals, such as community usage, GitHub stars/forks/contributors, independent references or discussions, institutional backing, and sustained public engagement.

That decision was about the Foundation program's public-visibility threshold, not a technical rejection of AutoModSync or its build/signing design. The project may reapply after broader public adoption. A regular paid SignPath subscription or another trusted Authenticode provider could also be used independently of the Foundation program.

## Current public release path: GitHub Actions + Sigstore provenance

The canonical build workflows are:

- `.github/workflows/release-build.yml`
- `.github/workflows/distribution-packages.yml`

They run on GitHub-hosted Windows runners and:

1. check out the exact source revision;
2. install the freely downloadable Valheim Dedicated Server through SteamCMD for compile-time game references;
3. build the standalone and store packages from that checked-out source;
4. generate SHA-256 manifests;
5. create GitHub/Sigstore build-provenance attestations for the generated package bytes;
6. upload the packages and checksum manifests as GitHub Actions artifacts.

The manual Nexus, CurseForge, and Thunderstore publication workflows also attest the **exact package bytes produced by that publishing run** before upload to the store. This matters because independently rebuilt ZIP files are not assumed to be byte-identical.

For a downloaded artifact, verify provenance with:

```powershell
gh attestation verify .\ValheimAutoModSync-2.6.0.zip -R GordonFreesay/ValheimAutoModSync
```

The same command works for the Nexus, CurseForge, and Thunderstore ZIP filenames. The canonical `SHA256SUMS.txt` is also attested.

A successful attestation verification proves that the exact downloaded digest was attested by a GitHub Actions workflow for this repository. It does not prove that Windows trusts the binary as an Authenticode publisher.

## Retained optional SignPath integration

The workflow still contains conditional SignPath submission steps so the integration does not have to be rebuilt if trusted SignPath signing becomes available later.

Those steps run only on a manually dispatched workflow and only when all required SignPath credentials are configured:

### GitHub Actions secret

- `SIGNPATH_API_TOKEN`

### GitHub Actions repository variables

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY_SLUG`

Without those credentials, the workflow leaves the PE files Authenticode-unsigned but still generates GitHub/Sigstore provenance for the official GitHub-built artifacts.

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

A local build also does not receive GitHub/Sigstore provenance merely because it was built from the repository. Provenance attestations are minted only inside the authorized GitHub Actions workflows and are tied to the exact artifact digest produced there.

Never commit signing credentials, private keys, PFX files, API tokens, or signing metadata.
