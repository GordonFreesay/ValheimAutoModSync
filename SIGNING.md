# AutoModSync release signing

AutoModSync 2.5.0 is staged so public releases can be Authenticode-signed before packaging.

Supported signing backends:
- Microsoft Artifact Signing / Trusted Signing via SignTool `/dlib` and metadata.
- A CA-issued code-signing PFX.
- A trusted code-signing certificate already installed in the Windows certificate store.

A self-signed certificate is useful for local testing but is not a SmartScreen reputation solution.

## Files signed

Only AutoModSync-authored PE files are signed:
- `ValheimAutoModSync.Client.dll`
- `ValheimAutoModSync.Server.dll`
- `ValheimAutoModSync.Apply.exe`
- `AutoModSync.BuildTool.exe`

Third-party BepInEx / Unity Doorstop files, including `winhttp.dll`, are not re-signed.

## Public signed release

Configure exactly one signing mode, then run:

```text
build-signed-release.bat
```

This sets `AMS_REQUIRE_SIGNING=1`, builds both release formats, and fails if signing or signature verification fails.

Normal development builds remain usable without a certificate. If a signing identity is configured they sign automatically; otherwise they are explicitly unsigned development builds.

## Artifact Signing / Trusted Signing

```bat
set "AMS_ARTIFACT_SIGNING_DLIB=C:\Path\To\Azure.CodeSigning.Dlib.dll"
set "AMS_ARTIFACT_SIGNING_METADATA=C:\Secure\artifact-signing-metadata.json"
```

If SignTool is not discoverable automatically:

```bat
set "AMS_SIGNTOOL=C:\Path\To\signtool.exe"
```

## PFX

```bat
set "AMS_SIGN_PFX=C:\Secure\GordonFreesay-CodeSigning.pfx"
set "AMS_SIGN_PFX_PASSWORD=your-password-if-required"
```

## Windows certificate store

```bat
set "AMS_SIGN_THUMBPRINT=0123456789ABCDEF..."
```

Set `AMS_SIGN_MACHINE_STORE=1` for Local Machine instead of Current User.

Use `AMS_TIMESTAMP_URL` to override the RFC3161 timestamp server.

Never commit signing credentials, private keys, PFX files, or Artifact Signing metadata.

Signing improves publisher identity and gives SmartScreen a stable publisher reputation path, but does not guarantee every brand-new file will immediately be reputation-whitelisted.
