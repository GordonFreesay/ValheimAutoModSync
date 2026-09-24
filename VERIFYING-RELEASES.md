# Verifying AutoModSync releases

AutoModSync uses two separate trust layers:

1. **Windows Authenticode** — identifies a Windows software publisher to Windows. AutoModSync does not currently have a publicly trusted Authenticode publisher certificate.
2. **GitHub/Sigstore build provenance** — cryptographically binds the exact bytes produced by an official GitHub Actions workflow to this repository, workflow, commit, and build event.

Starting with the 2.6 release pipeline, official GitHub-built standalone and store packages are SHA-256 hashed and attested with GitHub Artifact Attestations.

## Verify provenance

Install GitHub CLI, then verify the exact file you downloaded:

```powershell
gh attestation verify .\ValheimAutoModSync-2.6.0.zip -R GordonFreesay/ValheimAutoModSync
```

For store packages, use the downloaded filename instead:

```powershell
gh attestation verify .\ValheimAutoModSync-2.6.0-Nexus.zip -R GordonFreesay/ValheimAutoModSync
gh attestation verify .\ValheimAutoModSync-2.6.0-CurseForge.zip -R GordonFreesay/ValheimAutoModSync
gh attestation verify .\GordonFreesay-ValheimAutoModSync-2.6.0.zip -R GordonFreesay/ValheimAutoModSync
```

A successful result means GitHub found and cryptographically verified an attestation for the exact artifact digest under this repository.

## Verify SHA-256 manually

The canonical GitHub release includes `SHA256SUMS.txt`.

Calculate the digest:

```powershell
(Get-FileHash -Algorithm SHA256 .\ValheimAutoModSync-2.6.0.zip).Hash.ToLowerInvariant()
```

Compare it with the matching line in `SHA256SUMS.txt`.

The checksum manifest itself can also be provenance-verified:

```powershell
gh attestation verify .\SHA256SUMS.txt -R GordonFreesay/ValheimAutoModSync
```

## What this does and does not prove

GitHub/Sigstore provenance proves that the exact bytes were attested by an authorized GitHub Actions workflow for `GordonFreesay/ValheimAutoModSync`.

It does **not**:

- make an unsigned PE file Authenticode-signed;
- suppress Windows SmartScreen or unknown-publisher warnings;
- require users to install a custom root certificate;
- attest a local rebuild merely because it came from the same source tree.

Local builds print their own SHA-256 manifest but explicitly state that they are not GitHub-attested.

## Store downloads

The Nexus, CurseForge, and Thunderstore publication workflows attest the exact ZIP produced in the same workflow run before it is uploaded. Verification therefore targets the bytes downloaded from that store, not a separately rebuilt package with the same version number.

If a store changes/repackages uploaded bytes, direct attestation verification will fail. In that case use the canonical GitHub standalone release or compare against the published release information before trusting the file.

## Authenticode status

Trusted Authenticode signing remains a separate future improvement. The repository keeps the optional SignPath/trusted-signing integration documented in `SIGNING.md`, but provenance does not pretend to be Authenticode.

Never install an untrusted self-signed root certificate merely to silence a publisher warning.
