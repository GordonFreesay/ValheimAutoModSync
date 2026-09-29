# AutoModSync 2.6.1 updater addendum

2.6.1 supersedes the 2.6.0 standalone Apply-helper packaging described in older evidence below. Production releases no longer contain `ValheimAutoModSync.Apply.exe`. The same PREPARED/COMMITTED transaction engine is linked into the visible `ValheimAutoModSyncInstaller.exe`; it waits for Valheim to exit normally and never force-terminates it. Transaction fault-injection tests compile a disposable test-only entry point around that exact engine and never create a release/package helper executable.

The 2.6.1 live release gate additionally requires a real updater apply/restart/reconnect, a 2.6.0 -> 2.6.1 migration that installs the updater before the new client relies on it, and confirmation that final GitHub/Nexus/CurseForge archives contain no standalone Apply helper.

## 2.6.1 live release-gate result — 2026-09-29

PASS. A real released 2.6.0 standalone client completed the password-protected 2.6.1 migration path against the development 2.6.1 server build.

Observed boundary:
- before normal Valheim server access was granted, the 2.6.0 bridge disclosed no protected AMS manifest/config/bundle state;
- after the correct server key was accepted, the server issued a short-lived one-use migration grant bound to the same platform identity and intentionally disconnected the bootstrap connection;
- the following reconnect consumed that migration-only grant, entered signed AMS synchronization, installed the 2.6.1 client plus the single visible installer/updater, applied the staged transaction, restarted Valheim, and reconnected;
- the migrated client reported version 2.6.1 and then completed a normal 2.6.1 protected-server join successfully.

Released 2.6.0 does not automatically initiate the migration-grant reconnect itself, so one manual reconnect is required after the intentional bootstrap disconnect. The grant lifetime is five minutes; an expired grant correctly requires the server-access bootstrap again.

Final local 2.6.1 standalone, Nexus, CurseForge, and Thunderstore-format package builds passed the first-party PII artifact scan. Package inspection confirmed no release ZIP contains `ValheimAutoModSync.Apply.exe`. The Thunderstore-format artifact remains a build/compatibility artifact for 2.6.1 unless separate publication-policy clearance is confirmed.

## Final tagged-artifact gate

- [x] **PASS — 2026-09-29.** `v2.6.1` was tagged at `e409ef2a2919853bb965d1e49867cf2e8be802f5`; the tag-triggered Distribution Packages workflow completed successfully and attested the package digests plus `SHA256SUMS.txt`. The published GitHub Release `Valheim AutoModSync 2.6.1` contains `ValheimAutoModSync-2.6.1.zip` with SHA-256 `03a68d77ddd037053fa240f4517ea6961f50b05e320688fdf5bc36710f4a29fa`, matching the tagged workflow manifest, and `SHA256SUMS.txt` with SHA-256 `f83ba7b2affc78c6730498e6db22e86c27e87ee594c2acf23b56d46e07ab7b76`. The checksum manifest's GitHub/Sigstore attestation was independently verified with `gh attestation verify` before publication; the release asset digest is the exact standalone digest attested by the tagged Distribution Packages workflow.

