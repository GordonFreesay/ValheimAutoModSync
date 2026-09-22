# AutoModSync 2.6 validation checklist

**Status:** NOT RUN. This file records required evidence; it does not claim that any 2.6 test has passed.

Release builds/publication remain deferred until the maintainer has functionally validated the implementation.

## Baseline

- Development branch: `dev/2.6`
- Baseline main commit: `cf73fd97e1a13ac293783d722e112b14d83b2d17`
- Protocol baseline: AMS4 / protocol 4
- Public release during development: 2.5.0

## Phase 1 — security/resource foundation

### Compatibility / preflight

- [ ] Non-AutoModSync server receives no AMS response and the original `ServerHandshake` is replayed after the discovery timeout.
- [ ] Valid 2.6 AutoModSync server with matching client files establishes/uses trust, then replays the original `ServerHandshake` unchanged.
- [ ] Jotunn/Epic Loot/other compatibility checks still run after successful AMS preflight.
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
