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

