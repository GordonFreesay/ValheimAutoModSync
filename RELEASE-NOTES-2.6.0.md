# Valheim AutoModSync 2.6.0

AutoModSync 2.6 is a large reliability, scale, safety, and UX release. It keeps the existing AMS4 / protocol 4 compatibility baseline while adding negotiated 2.6 capabilities.

## Highlights

- **Large public-server payloads:** content-addressed immutable ZIP caching, startup prewarming, and single-flight construction let identical fresh clients reuse the same prepared bundle instead of recompressing it per connection.
- **Bounded concurrent transfers:** a server-wide FIFO scheduler limits active/queued work, shares an aggregate bandwidth budget round-robin, observes Steam reliable-queue pressure, and keeps per-client transfer state bounded.
- **True interrupted-download resume:** the client retains one bounded verified partial package and resumes only when the server independently verifies the exact retained prefix against its current immutable artifact.
- **Client-only server payloads:** server operators can distribute files from `BepInEx/AutoModSync/ClientPayload/plugins/**` without making the dedicated server load those files itself.
- **Ownership-safe cleanup:** AutoModSync records only files it actually installed/replaced. Stale files are removed only when their current bytes still exactly match the same trusted server's last-owned digest; locally modified, unrelated, pre-existing, and cross-server files are preserved.
- **Transactional apply and recovery:** PREPARED/COMMITTED journals, verified backups, rollback, and retry protect live files across crashes or interrupted restarts.
- **Branded synchronization UX:** comparison counts, queue position, current/average throughput, ETA, resume-retained bytes, verification, apply, restart, reconnect, completion, and bounded failure states are visible in the AMS panel.
- **Safer trust presentation:** first contact shows a short human-comparison security code instead of exposing the complete fingerprint in normal UI/logs. The full 256-bit fingerprint is still used internally for signature validation and pinning.
- **Server-browser AMS badge:** Steam-backed servers can passively advertise AMS version/protocol rules; AMS clients can show a small local badge beside positively identified servers without rewriting server names.
- **Installer overhaul:** AMS-branded installer, live complete/partial detection, Repair / Update, and conservative role-aware uninstall. Shared BepInEx and unrelated files are preserved; server signing identity/config are preserved unless explicitly selected for removal.
- **Release provenance:** official GitHub-built packages receive SHA-256 manifests plus GitHub/Sigstore build-provenance attestations.

## Security and failure behavior

- Non-AMS servers still fail open to normal Valheim after the short discovery window.
- Once a server positively responds as AutoModSync, signature/trust/path/resource/transfer/apply-preparation failures fail closed for that protected join.
- Client/server/apply enforce fixed synchronization roots, Windows reserved-name/trailing-dot-space defenses, reparse-point rejection, bounded bundle/file counts and sizes, and bounded ZIP expansion.
- Server signing identity files and loader-wide `BepInEx.cfg` remain excluded from synchronized config manifests.

## Compatibility

- Protocol remains **AMS4 / 4**.
- 2.6 retains negotiated transfer fallbacks for older AMS4 peers.
- Store/package-manager installs continue to protect package-manager-owned AutoModSync binaries from server self-overwrite.

## Verification

Official GitHub-built release files can be checked with:

```powershell
gh attestation verify .\ValheimAutoModSync-2.6.0.zip -R GordonFreesay/ValheimAutoModSync
```

The release also includes `SHA256SUMS.txt`. GitHub/Sigstore provenance proves build origin/integrity for the exact artifact digest; it is separate from Windows Authenticode and does not suppress SmartScreen/unknown-publisher warnings.

See `VERIFYING-RELEASES.md` for the standalone and store-package verification commands.

## Installation

Standalone users: close Valheim and any dedicated server process, extract `ValheimAutoModSync-2.6.0.zip`, and run `ValheimAutoModSyncInstaller.exe`.

Nexus Mods / CurseForge / Thunderstore users should use the package for that store. All channels use the same AutoModSync version.
