# AutoModSync 2.6.1 updater addendum

2.6.1 supersedes the 2.6.0 standalone Apply-helper packaging described in older evidence below. Production releases no longer contain `ValheimAutoModSync.Apply.exe`. The same PREPARED/COMMITTED transaction engine is linked into the visible `ValheimAutoModSyncInstaller.exe`; it waits for Valheim to exit normally and never force-terminates it. Transaction fault-injection tests compile a disposable test-only entry point around that exact engine and never create a release/package helper executable.

The 2.6.1 live release gate additionally requires a real updater apply/restart/reconnect, a 2.6.0 -> 2.6.1 migration that installs the updater before the new client relies on it, and confirmation that final GitHub/Nexus/CurseForge archives contain no standalone Apply helper.

