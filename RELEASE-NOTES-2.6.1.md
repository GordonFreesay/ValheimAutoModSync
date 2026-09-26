# Valheim AutoModSync 2.6.1

AutoModSync 2.6.1 is a single-purpose access-control patch over 2.6.0. It keeps **AMS4 / protocol 4** and the existing 2.6 synchronization, transfer, apply, restart, and reconnect behavior.

## Password-protected servers

A password-protected server now withholds all protected AutoModSync synchronization state until the **exact live connection** proves the correct Valheim server password.

Before authentication, AMS may disclose only that AutoModSync is present, its product version/protocol, and that authentication is required. It does **not** disclose the server signing fingerprint/public key, manifest dimensions or contents, mod/config filenames or paths, hashes, sizes, synchronized configuration, bundle identifiers/content, cache/build details, or transfer capabilities.

The client still uses Valheim's normal password dialog. AutoModSync converts that submission into Valheim's existing salted password proof; the plaintext password is not sent in `AMS4_Auth`. Authorization is keyed to the current `ZRpc` connection and is discarded when that connection ends.

Public/no-password servers retain the 2.6.0 preflight behavior.

## Compatibility boundary

The original Valheim `ServerHandshake` remains held until AutoModSync synchronization finishes. On passworded servers, this also keeps Jotunn/other compatibility exchange behind password authentication so the normal mod-validation phase cannot reveal or reject against server mod state before AMS is allowed to run.

AutoModSync 2.6.0 and earlier clients fail closed on password-protected 2.6.1 servers because they do not implement the password-proof capability. Public servers remain compatible through AMS4/protocol 4.

## First-contact permission acknowledgement

The first server-fingerprint trust dialog now makes the **Yes** action also confirm that the user has permission to receive the mods/configuration supplied by that server.

The dialog also states that **AutoModSync is not responsible for verifying or enforcing third-party mod licensing or redistribution requirements**. AutoModSync provides transport, verification, and synchronization mechanics; it does not grant redistribution rights.

## Scope

No unrelated 2.6.2 work is included in this patch.
