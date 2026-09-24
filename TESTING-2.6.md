# AutoModSync 2.6 validation checklist

**Status:** PARTIAL RUNTIME VALIDATION. Only explicitly checked items and the dated runtime evidence below have passed; all other items remain pending.

Release builds/publication remain deferred until the maintainer has functionally validated the implementation.

## Baseline

- Development branch: `dev/2.6`
- Baseline main commit: `cf73fd97e1a13ac293783d722e112b14d83b2d17`
- Protocol baseline: AMS4 / protocol 4
- Public release during development: 2.5.0

## Phase 1 — security/resource foundation

### Compatibility / preflight

- [ ] Non-AutoModSync server receives no AMS response and the original `ServerHandshake` is replayed after the discovery timeout.
- [x] Valid 2.6 AutoModSync server with matching client files establishes/uses trust, then replays the original `ServerHandshake` unchanged.
- [x] Jotunn/Epic Loot/other compatibility checks still run after successful AMS preflight.
- [x] Restart reconnect remains one-shot: after the first outgoing reconnect is created, later character-selection callbacks do not dispatch a second connection.
- [ ] 2.6 client can still use the existing AMS4 transfer fallbacks with a 2.5 server.

### Recognized AMS must fail closed

For every case below, verify that no normal ServerHandshake is replayed and no live synchronized file is changed:

- [ ] Invalid/malformed `AMS4_Ack`.
- [ ] Server acknowledges AMS but never begins a manifest.
- [ ] Invalid manifest header.
- [ ] Missing/out-of-order manifest part.
- [ ] Invalid RSA/SHA-256 manifest signature.
- [ ] User declines first-contact server trust.
- [ ] Server-reported AMS error after recognition.
- [ ] Invalid/oversized bundle header.
- [ ] Out-of-order/oversized legacy bundle chunk.
- [ ] Out-of-order/oversized binary bundle batch.
- [ ] Wrong final bundle SHA-256/size/file count.
- [ ] Apply/restart preparation failure after a verified download.

### Path boundary

Create fixtures only inside a disposable test Valheim/BepInEx tree.

- [ ] Manifest path containing `..` is rejected.
- [ ] Rooted/drive-style path is rejected.
- [ ] Windows device-name segment such as `CON.dll`, `NUL.txt`, `COM1.json` is rejected.
- [ ] Segment ending in a period is rejected.
- [ ] Segment ending in a space is rejected.
- [ ] Control/invalid filename character is rejected.
- [ ] Excessive segment/path length is rejected.
- [ ] Server scanner does not traverse a junction/reparse-point directory under plugins/patchers/config.
- [ ] Server scanner does not publish a reparse-point file.
- [ ] Client refuses a live destination whose existing parent/leaf is a reparse point.
- [ ] Apply helper independently refuses a staging/live path redirected through a reparse point.

### Resource limits / ZIP extraction

- [ ] Client rejects a compressed bundle declaration over 2048 MiB before opening/writing the staging archive.
- [ ] Client rejects more than 4096 required bundle files.
- [ ] Client rejects more than 4096 MiB expanded required content.
- [ ] Client rejects an individual required file over the 512 MiB hard client ceiling.
- [ ] Client rejects an implausible/excessive bundle chunk count.
- [ ] Client rejects non-hex or non-64-character bundle SHA-256 text.
- [ ] Legacy Base64/binary batch bytes cannot write past the bundle's declared compressed size.
- [ ] ZIP entry whose expanded stream exceeds its signed size is stopped during extraction.
- [ ] Cumulative ZIP expansion cannot exceed the client expanded-size ceiling.
- [ ] Partially extracted failed entry is not left as an accepted pending file.
- [ ] Server rejects requested expanded source bytes above `MaxExpandedBundleMiB` before ZIP construction.
- [ ] Server aborts bundle construction once compressed output crosses `MaxBundleMiB`.
- [ ] Failed bundle construction removes its unpublished temporary ZIP.

## Phase 2 — transactional apply/recovery

- [x] Normal verified apply creates a transaction journal, reaches `PREPARED` before the first live write, reaches `COMMITTED` only after every destination verifies, removes `pending.txt`/staging/journal during cleanup, then relaunches/reconnects normally.
- [x] Existing destination files are durably backed up under `BepInEx/AutoModSync/apply-transaction/backup` before any synchronized live file changes; rollback verifies and restores those backups before retry.
- [x] Files that did not exist before the transaction are represented as originally absent and are deleted during rollback if a partial apply created them.
- [x] Killing the apply helper after `PREPARED` but before `COMMITTED` leaves the journal/backups/pending staging intact; the next launch hands recovery back to the helper, rolls back the complete old set, then retries the complete new set before normal play continues.
- [x] Killing the apply helper after `COMMITTED` but before cleanup never rolls back to old content; the next helper invocation preserves the complete new set and finishes cleanup.
- [x] A caught per-file apply failure after `PREPARED` rolls every previously touched destination back to its verified old backup before the helper exits with failure.
- [x] Transaction recovery rejects a malformed/version-mismatched journal rather than guessing destinations.
- [x] Transaction recovery rechecks fixed-root/reparse protections and still refuses protected config names (`BepInEx.cfg`, `ValheimAutoModSync.private.xml`, `ValheimAutoModSync.public.xml`).
- [x] `apply.log` records PREPARED/COMMITTED/rollback milestones sufficient to establish which recovery side won in normal and interrupted transactions.
- [x] An interrupted transaction is never treated as a successful AMS state: the client detects `pending.txt` or `apply-transaction`, starts the external helper, and exits/restarts before attempting a server join.

Development-only deterministic interruption hooks are compiled into `ValheimAutoModSync.Apply.exe` only by `build-dev.bat` (symbol `AMS_DEV_TESTS`). They are absent from release builds. To pause after a real partial apply, create `BepInEx/AutoModSync/apply-test-pause-after-items.once` containing an integer from 1 to one less than the pending file count. The helper consumes the marker after PREPARED, applies that many files, logs `DEV TEST PAUSE ... before COMMITTED`, and sleeps for 120 seconds so the process can be terminated. To inspect restored pre-existing files after rollback but before retry, create `apply-test-pause-after-rollback.once`; the helper consumes it after rollback has completed and the old state is live again. To pause after COMMITTED but before cleanup, create `apply-test-pause-after-committed.once`.

Additional development-only markers support the remaining adversarial gates: `apply-test-pause-after-prepared.once` pauses after durable PREPARED but before the first live write, and `apply-test-fail-after-items.once` contains an item count at which the helper throws a caught apply failure after that many verified live writes. `test-phase2-adversarial.ps1` exercises the caught-failure rollback, malformed/version-mismatched journal rejection, protected-config/fixed-root rejection, and transaction reparse-point rejection entirely inside an isolated `%TEMP%` BepInEx tree. Run `build-dev.bat` first, then run the harness; do not mark the remaining checkboxes passed until its observed output reports all four checks as PASS.

## Phase 3 — cached/single-flight bundle construction

- [ ] Dedicated-server startup with `PrebuildFreshClientBundle=true` constructs the nearly-bare-client baseline before normal joins, logs one prewarm MISS/build timing, and retains it for the first real client even if ordinary `BundleCacheSeconds` elapses.
- [x] A nearly-bare client with the current AutoModSync client DLL receives a startup-prewarmed `cache=HIT` with no join-time ZIP rebuild.
- [ ] First request for a different changed-file set logs `cache=MISS`, publishes one immutable content-addressed ZIP, and reports ZIP-build/SHA-256 preparation timings.
- [ ] A second fresh client requesting the identical signed file set within `BundleCacheSeconds` logs `cache=HIT`, uses the same cache key/SHA-256/size, and performs no second ZIP build.
- [ ] Two overlapping identical fresh-client requests produce one build; the follower logs `WAIT` / `WAIT-HIT` and both transfers read the same published artifact.
- [ ] Changing any requested file changes the content-derived cache key and causes a new MISS; stale content is never returned under the old key.
- [ ] A same-size source-file change after manifest signing aborts the build during source re-hash rather than publishing a poisoned cache artifact.
- [ ] `BundleCacheSeconds = 0` permits active sharing but removes the artifact after the last active transfer releases it.
- [ ] TTL expiry removes only idle artifacts.
- [ ] `BundleCacheMaxMiB` evicts idle least-recently-used artifacts without deleting an artifact referenced by an active transfer.
- [ ] Failed ZIP construction leaves no published cache entry and removes its private `bundle-build-*.tmp` file.
- [ ] Server restart deletes orphaned prior-process bundle ZIP/temp files and rebuilds rather than trusting stale cache metadata.
- [x] First-contact trust remains responsive for longer than Valheim's normal ZRpc timeout window: the background native Windows Yes/No dialog does not block Unity/ZRpc networking, and accepting after an intentional delay continues the same AMS session.
- [x] If the recognized AMS socket is lost while first-contact trust or package preparation is visible, the transient trust/progress UI clears automatically and the protected join remains fail-closed.
- [x] During first-contact trust, the native Windows dialog owns normal mouse input without AutoModSync changing `Cursor.visible` or `Cursor.lockState`; Yes continues synchronization and No aborts the protected join.
- [ ] If the protected connection ends while the background native trust dialog itself is still open, the stale dialog/result is dismissed or ignored and cannot apply to a later connection.
- [x] Rejecting trust shows the blocked-join status only transiently and removes it from the main menu after approximately four seconds.

## Phase 4 — exact-artifact resumable transfer

- [x] `test-phase4-resume.ps1` compiles the production resume/path-safety sources and passes deterministic interruption emulation without touching the real Valheim/BepInEx install.
- [x] A partial whose last write ends inside a chunk is truncated to the prior complete chunk boundary before it is offered for resume.
- [x] The client offers resume only for the same trusted server fingerprint and exact signed requested file set; the deterministic harness passed both wrong-change-set and distinct-server-fingerprint rejection.
- [x] The server accepts a nonzero resume offset only when bundle SHA-256, size, chunk size/count, file count, and SHA-256 of the exact retained prefix all match the current immutable artifact.
- [x] A corrupt retained prefix is rejected and the transfer restarts from chunk zero; corrupt bytes are never appended into an accepted bundle.
- [x] If the server rebuilds/changes the ZIP or transfer chunk geometry, the old partial is rejected and transfer restarts safely from zero.
- [x] A fully received bundle whose completion RPC was lost can resume at `TotalChunks`, receive only bundle completion, and still pass the full bundle SHA-256 before extraction.
- [x] Recognized connection loss after at least one complete chunk closes/flushed the stream but preserves the single bounded resume slot instead of deleting it.
- [x] Server-side dead-peer cleanup restores temporary Steam transport settings and releases the old cache artifact reference after an interrupted transfer.
- [x] Development one-client interruption marker `BepInEx/AutoModSync/resume-test-disconnect-after-chunks.once` forces a real mid-download socket close; manual reconnect offers the retained prefix, the server logs exact-prefix verification/acceptance, and the client continues from a nonzero chunk.
- [x] After resumed completion the full ZIP SHA-256, ZIP extraction checks, transactional apply, restart, and normal reconnect still pass exactly as for a fresh transfer.
- [x] Older AMS4 peers remain compatible: resume fields are sent/read only when both sides negotiate `bundle-resume1`; protocol stays AMS4 / 4.

Development-only compatibility emulation is available without a second tester. Create client marker `BepInEx/AutoModSync/resume-test-emulate-legacy-client.once` before a one-file delta join to make that connection behave like a pre-resume AMS4/2.5 client: it advertises `roots1` but not `bundle-resume1` and ignores a server resume advertisement. Create server marker `BepInEx/AutoModSync/resume-test-emulate-legacy-server.once` before a separate one-file delta join to make that peer behave like a pre-resume AMS4 server: the acknowledgement omits `bundle-resume1` and the server neither reads nor writes resume extension fields. Both markers are compiled only under `AMS_DEV_TESTS`; release binaries do not contain these emulators. The server emits explicit development evidence for both cases: `legacy-server compatibility confirmed: original AMS4 bundle request/header shape active` when emulating the old server, and `legacy-client compatibility observed: peer omitted bundle-resume1; original AMS4 bundle request/header shape active` when the client emulates 2.5.

## Phase 5 — concurrent transfer scheduler / aggregate server budget

- [x] `test-phase5-scheduler.ps1` compiles the production scheduler class and passes deterministic multi-peer admission/bandwidth tests without loading Valheim or touching BepInEx.
- [x] Eight emulated peers with `MaxActiveBundleTransfers=4` produce exactly four active slots and four FIFO waiters; a fifth peer cannot bypass the active limit.
- [x] Releasing an active slot promotes exactly the oldest waiting peer and advances the remaining queue positions.
- [x] The server-wide token bucket stays within `AggregateSendRateMaxBytesPerSec` plus its bounded 250 ms burst envelope.
- [x] Four continuously demanding emulated peers receive round-robin grants without starvation; the deterministic fastest/slowest share ratio remains below 1.10.
- [x] A refunded grant (the path used when Steam queue backpressure blocks a send) restores both aggregate tokens and peer demand while advancing the next fairness turn.
- [x] Removed/disconnected queued peers are lazily skipped without corrupting later FIFO order.
- [x] `build-dev.bat` compiles the integrated client/server runtime with `AutoModSync.TransferScheduler.cs`.
- [x] A real one-client delta sync is admitted through the scheduler, receives the bundle, verifies/applies/restarts/reconnects normally, and restores temporary Steam settings.
- [ ] With a deliberately lowered aggregate cap, measured one-client payload goodput respects the configured scheduler ceiling within expected framing/timing tolerance.
- [ ] Steam reliable-queue backpressure can defer a scheduler grant without disconnecting/failing the client; ordinary transfer resumes when the queue falls below the configured envelope.
- [x] Deterministic production-policy validation confirms idle expiry fires only for active peers with no outstanding scheduler demand; queued clients have no idle-expiry path.
- [x] Waiting admission is bounded by `MaxQueuedBundleTransfers`; the production scheduler rejects a peer beyond active+queued capacity before the server can acquire/build another artifact.
- [ ] Older AMS4 peers still transfer successfully without `bundle-scheduler1`; they wait silently if queued and never receive the optional `AMS4_QueueStatus` RPC.
- [ ] Real 2/4/8-client qualification remains pending until multiple independent clients are available. Deterministic emulation proves scheduler policy, not aggregate NIC/Steam/game-loop behavior under true concurrent sockets.

## Runtime evidence — 2026-09-23

- Phase 5 integrated development build passed locally after the scheduler integration was added. `build-dev.bat` reported `SUCCESS: development runtime built in: C:\Users\billy\source\repos\oG1337\ValheimAutoModSync\DevBuild` and explicitly produced no installer, release ZIP, store package, tag, or publication artifact.
- Phase 5 one-client live scheduler integration passed on 2026-09-23 with a one-file delta. The server logged scheduler admission (`queuePosition=1, active=0/4`), built the 4.0 KB bundle, applied temporary Steam transport tuning, published `bundle ready` with `schedulerQueue=0.018 s`, emitted transfer telemetry, and restored the exact Steam bundle transport settings after synchronization. The restarted client then executed its one-shot reconnect, reported that its mods already matched the trusted server, and released the held Valheim ServerHandshake. This closes the basic integrated admission -> transfer -> apply/restart/reconnect path for one client.
- Phase 5 deterministic scheduler CI initially exposed a fairness bug: the round-robin cursor advanced when the aggregate token bucket was short, allowing refill boundaries to skip a peer's turn. The scheduler was corrected to retain the current peer until enough tokens exist for its fixed-size grant. The expanded rerun on Windows Server 2025 now passes all 8/8 checks: 8-peer/4-slot admission, FIFO promotion, aggregate rate+burst enforcement, four-peer fairness, blocked-grant refund behavior, queued-peer removal, bounded active+waiting admission, and idle-expiry semantics that protect outstanding demand. This validates the production scheduler policy class independently of Valheim; remaining live work is aggregate-cap/backpressure measurement and post-scheduler old-peer compatibility.
- Phase 4 deterministic resume-state harness passed all 7/7 checks: non-boundary interrupted-write truncation to a complete chunk plus exact-byte resume; corrupt-prefix rejection; wrong-change-set rejection; changed-artifact and changed-chunk-geometry rejection; fully-downloaded/lost-completion resume at TotalChunks with exact full SHA-256; expired-state discard; and malformed/orphaned metadata rejection/cleanup. The harness compiled the production resume/path-safety sources and reported that no real Valheim installation or server files were modified. Distinct-server-fingerprint rejection and live ZRpc/Steam interruption/reconnect behavior remain separate runtime gates.
- A one-client live Phase 4 run on 2026-09-23 proved the full interruption/resume path. The first connection used the startup-prewarmed 313.4 MB bundle from cache and was deliberately closed mid-transfer. The server then restored its temporary Steam transport settings and logged `AutoModSync released an interrupted bundle transfer after peer disconnect.`. On the next connection the same cache artifact was a HIT and the server logged `AutoModSync exact-artifact resume accepted at chunk 4096/13374 (96.0 MB retained, prefixVerify=1.340 s).`, proving a nonzero retained prefix survived the disconnect and was independently verified against the immutable server artifact. The resumed transfer completed, transport settings were restored again, the client applied the synchronized files, restarted, performed its one-shot reconnect, reported that client mods already matched the trusted server, and released the held Valheim handshake. This closes the live recognized-loss preservation, dead-peer cleanup, nonzero resume, and end-to-end resumed apply/restart/reconnect gates.
- AMS4 compatibility emulation passed in both directions on 2026-09-23 using one-file live deltas. With the server emulating pre-resume AMS4, it logged `AutoModSync DEV TEST legacy-server compatibility confirmed: original AMS4 bundle request/header shape active.`, built the one-file bundle, transferred it, restored Steam transport settings, and the client subsequently relaunched into its one-shot reconnect flow. With the client emulating pre-resume AMS4/2.5, the server logged `AutoModSync DEV TEST legacy-client compatibility observed: peer omitted bundle-resume1; original AMS4 bundle request/header shape active.` and successfully prepared the one-file bundle using the original request/header shape. This closes the old-peer compatibility gate without changing protocol version 4.
- The updated Phase 4 deterministic harness was rerun independently on GitHub Actions Windows Server 2025 at commit `5da0f66647af5f01511c85360b2ffac4025ed739`. All 7/7 cases passed, including `Different trusted server or change-set metadata cannot reuse the saved partial`, which now covers both a changed signed request key and a distinct trusted-server fingerprint. This closes the final Phase 4 validation item.

- Phase 2 normal transaction retest on 2026-09-23 applied a real 60-file server payload. The helper logged `Transaction PREPARED` before the first `Applied 1/60` line, verified all 60 destinations, then logged `Transaction COMMITTED; complete new state verified.`, `Committed transaction cleanup complete.`, and `Apply state is clean; relaunching Valheim.`. PREPARED occurred about 0.20 s after helper start; COMMITTED followed about 1.32 s later and cleanup completed about 0.03 s after COMMIT. This passes the normal transactional apply path but does not yet prove rollback/recovery under forced interruption.
- Because the real 60-file apply completes in roughly 1.5 seconds, manual process-kill timing is not reliable. `build-dev.bat` now compiles development-only one-shot fault-injection pauses so PREPARED partial-apply and post-COMMITTED cleanup interruption can be tested deterministically without shipping those hooks in release binaries.
- Deterministic PREPARED interruption retest on 2026-09-23 paused after 10/60 live files with no `committed.ok`; `pending.txt` and the transaction manifest/`prepared.ok` remained. After the helper was terminated and Valheim was started again, the client handed recovery back to the helper. The helper logged `Found interrupted PREPARED transaction; rolling back to complete old state.`, completed rollback, retried all 60 files from retained staging, reached COMMITTED, cleaned up, and relaunched. Final checks showed both `pending.txt` and `apply-transaction` absent. This fresh-client case specifically proves rollback of destinations that were originally absent; restoration of pre-existing destination backups remains a separate pending test.
- Pre-existing-file rollback retest on 2026-09-23 intentionally modified two already-synchronized PNG assets, then requested their two-file delta. The helper reached PREPARED, applied 1/2, paused before COMMITTED, and was terminated. On the next launch it logged `Found interrupted PREPARED transaction; rolling back to complete old state.` followed by `Rollback complete; staged new files remain available for a clean retry.`. The development pause after rollback then expired after 120 seconds, so the helper retried both files, reached COMMITTED, cleaned up, and relaunched. The helper's rollback path verifies every durable backup hash before restore and verifies each restored live file against the journaled old SHA-256/size before it can log rollback completion, so this run establishes successful restoration of pre-existing file contents even though an external hash snapshot was not taken during the pause. The relaunched client then reported its mods matched the trusted server and released the original Valheim handshake normally.
- Post-COMMIT interruption retest on 2026-09-23 changed one already-synchronized asset and armed the development pause after COMMITTED. The helper reached PREPARED, applied 1/1, wrote COMMITTED, then paused before cleanup and was terminated. On the next launch it logged `Found interrupted COMMITTED transaction; preserving complete new state and finishing cleanup.` followed by `Apply state is clean; relaunching Valheim.` No rollback occurred. The relaunched client then reported its mods already matched the trusted server and released the held Valheim handshake, confirming the committed new state survived the interrupted cleanup.
- The isolated `test-phase2-adversarial.ps1` harness passed all four remaining error-path groups on 2026-09-23: caught per-file failure rollback; malformed/version-mismatched journal rejection; protected-config plus fixed-root traversal rejection; and recovery rejection of a transaction-directory reparse point. The harness reported `PASS` for all four groups and confirmed it modified no real Valheim installation or server files. With these results, every Phase 2 checklist item is now observed as passed.

## Runtime evidence — 2026-09-22

Maintainer-provided client/server logs from development commit `37a4125f270a417489e9d620326c4f92e808bc26` establish the following smoke-test evidence:

- Fresh/near-bare client requested a real server mod set and received a **313.4 MiB compressed package**.
- Client verified and unpacked the package, persisted its reconnect token, closed cleanly for apply, relaunched, and automatically reconnected.
- Relaunched client loaded **AutoModSync Client 2.6.0**, then reported that its files already matched the trusted server before releasing the original Valheim handshake.
- Jotunn, ConditionalConfigSync, Warfare/Armory validation, and Epic Loot server-pushed data continued after AutoModSync preflight.
- Server restored the temporary SteamNetworkingSockets settings after the bundle transfer.
- With development transfer settings Min=8 MiB/s, Max=32 MiB/s, Buffer=16 MiB, Steam telemetry repeatedly reported exactly **8,388,608 B/s**, while the client measured **313.4 MiB in 46.8 s (6.70 MiB/s payload throughput)**.
- This evidence motivates the next controlled throughput run at Min=16 MiB/s, Max=64 MiB/s, Buffer=32 MiB. It does **not** qualify the concurrent-client scheduler, interruption/resume, adversarial protocol cases, or resource-limit cases.
- A later 2.6 post-reconnect log exposed a duplicate one-shot reconnect edge case: after the first trusted AMS reconnect succeeded and released the held handshake, a later `ShowCharacterSelection` callback scheduled another automatic character start and created a second outgoing connection. Source now suppresses character-selection scheduling once reconnect is finished or already pending, and clears any stale delayed start when the outgoing connection is created.
- Retest on 2026-09-22 with the patched client showed exactly one character-selection auto-start, one outgoing reconnect, one trusted AMS preflight, and normal downstream Jotunn/ConditionalConfigSync/Epic Loot processing. No second reconnect was dispatched.
- The same run exercised the 16/64/32 MiB development transfer settings. Server telemetry reported `SendRateMin 153600 -> 16777216`, `SendRateMax 153600 -> 67108864`, `SendBuffer 524288 -> 33554432`, and repeated live send-rate samples of exactly **16,777,216 B/s** before restoring the original Steam transport values. The provided post-reconnect/server logs do not contain the client's end-to-end payload timing, so this proves the Steam rate-floor change took effect but does **not** yet prove a corresponding wall-clock throughput improvement.
- Phase 3 cache retest on 2026-09-22 showed a real **60-file / 313.4 MiB** nearly-bare-client request returning `cache=HIT` with key `39c58994205f` and `prepare=0.000 s`; no join-time ZIP build occurred. The supplied server excerpt does not include the earlier startup-prewarm MISS/build timing, so startup construction duration remains separately unverified.
- The same test exposed a first-contact trust UX regression: leaving the old native fingerprint MessageBox open long enough caused the server to log a ZRpc timeout before the user accepted, after which the client could strand the AutoModSync preparation overlay on the failed session. Source now replaces that blocking native modal with an in-game non-blocking trust prompt and explicitly clears recognized-session UI/state if the active ZRpc dies.
- Retest confirmed the recognized-session disconnect cleanup half: stopping the server while the new trust prompt was visible caused the AutoModSync wording/prompt to disappear rather than remain stranded. That subcase is now passed.
- The same retest exposed an input-state issue: Valheim hides/locks its sword pointer during connection, so the in-game trust buttons rendered but were not clickable. A follow-up that reasserted `Cursor.visible=true` and `Cursor.lockState=None` every frame still conflicted with Valheim: the pointer rapidly flashed visible/invisible and could not move. Rejecting trust also exposed a separate UI-lifecycle bug where the `AutoModSync blocked this join` banner remained on the returned main menu indefinitely. Source now removes all AutoModSync cursor forcing, runs the native Yes/No trust dialog on a background STA thread so Unity/ZRpc processing continues while Windows owns the dialog pointer, ignores stale dialog results by generation, and makes the blocked-join banner self-expire after four seconds.
- Runtime retest on 2026-09-22 passed both normal decisions with the background native dialog: **No** aborted the protected join and the blocked banner cleared after about four seconds; **Yes** remained usable after an intentional delay beyond the prior timeout window and continued synchronization normally. The native-dialog-specific stale-result case where the server dies while that Windows dialog is still open remains separate and unverified.

## Evidence to record

For each manual/integration run, record:

- exact development commit SHA;
- Valheim build;
- BepInEx build;
- server/client installation mode (standalone or package-managed);
- relevant server config;
- client/server/apply-helper logs;
- expected result;
- observed result;
- before/after SHA-256 for any live file involved.

Do not change a checkbox to passed without actual test evidence.
