# Security Policy

## Supported versions

Security fixes are provided for the current AutoModSync release line. During the 2.6.1 release-candidate period, security reports should be evaluated against the current 2.6.1 branch as well as the latest published release.

## Reporting a vulnerability

Please avoid publishing exploit details before the issue can be investigated.

Preferred reporting path:

1. Use GitHub's **Security** tab and **Report a vulnerability** / private vulnerability reporting when available.
2. If private reporting is unavailable, open a minimal public issue that says you have a security concern and need a private contact method. Do not include working exploit details, secrets, server passwords, private signing keys, or personally identifying information in that issue.

Useful reports include the affected AutoModSync version, client/server role, Valheim/BepInEx versions, reproduction steps, relevant logs with secrets removed, and whether the issue occurs before or after the user has explicitly trusted the server.

## Security boundaries

AutoModSync treats the remote server and network input as untrusted until the relevant authentication, identity, integrity, path, and resource checks succeed.

Issues that should be reported as security vulnerabilities include, for example:

- bypassing the password boundary on a password-protected server;
- obtaining protected manifest/mod/config information before required authentication;
- replaying or transferring authentication state to another connection;
- bypassing server identity/signature verification or trust pinning;
- writing, replacing, or deleting files outside AutoModSync's documented synchronization roots;
- path traversal, alternate-path aliasing, or reparse/junction/symlink escape;
- causing the Apply helper to operate on destinations not proven by the verified AMS transaction state;
- bypassing SHA-256/manifest verification so unverified bytes become live;
- unauthorized stale-file deletion or cross-server ownership confusion;
- unbounded network/archive/preflight state that permits practical memory/disk exhaustion beyond documented limits;
- releasing stale compatibility/version traffic before AMS has completed the negotiated preflight boundary.

## Server signing-key storage

The persistent server signing credential is `BepInEx/config/ValheimAutoModSync.private.xml`. It is never synchronized to clients and is hard-blocked from AutoModSync config manifests.

On Windows, 2.6.1 also explicitly protects the private-key file at rest with a non-inheriting ACL. The allowed principals are:

- the exact Windows identity running the process that creates/loads the key;
- LocalSystem; and
- local Administrators.

Broad/inherited entries such as Users/Everyone are removed. The public-key file remains ordinary readable data.

The server re-applies and verifies this ACL before reading or using an existing private key. If ACL hardening/verification fails, AutoModSync fails closed and does not use that key to sign manifests. If a newly generated key cannot be secured, the just-created private/public identity files are removed rather than leaving an insecure credential behind.

A server intentionally run under a different Windows service account must ensure that account can access the private key before startup; once the server successfully starts under that account, AutoModSync re-hardens the key for that actual runtime identity.

## Explicit non-goals

AutoModSync verifies transport policy, server identity, integrity, synchronization scope, and installation state. It does **not** certify that arbitrary third-party BepInEx plugins are safe.

BepInEx plugins are executable code. Once a user explicitly trusts a server and permits its synchronized plugin files, those plugins can execute with the permissions of the Valheim process/user account. A deliberately malicious third-party plugin supplied by a server the user chose to trust is therefore outside the guarantee that AMS itself can provide, unless the malicious behavior depends on bypassing an AMS security boundary.

AutoModSync also does not determine, verify, grant, or enforce third-party mod licensing or redistribution rights. Server operators are responsible for ensuring that they are authorized to provide synchronized files to connecting clients.

## Disclosure

Please give the project a reasonable opportunity to reproduce and fix a reported vulnerability before public disclosure. Once a fix is available, the issue can be documented through the normal release notes or a GitHub security advisory as appropriate.
