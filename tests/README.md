# AutoModSync test and validation harnesses

This directory contains development, CI, live-runtime, installer, and release-qualification test gates for AutoModSync.

These scripts are intentionally kept out of the repository root so the public source tree separates product/build entry points from maintainer validation tooling.

## Groups

- `test-phase1-*` — path/resource/security/fail-closed and legacy AMS4 compatibility gates.
- `test-phase2-*` — transactional apply adversarial/recovery validation.
- `test-phase3-*` — immutable bundle cache, prewarm, stale-trust, and concurrency validation.
- `test-phase4-*` — exact-artifact resume validation.
- `test-phase5-*` — scheduler/backpressure fairness validation.
- `test-phase6-*` — ClientPayload, ownership, and stale-removal validation.
- `test-phase7-*` — synchronization UI, identity/privacy, browser badge, live transfer/reconnect, and resume gates.
- `test-installer-*` — standalone installer/uninstaller contract and disposable live gates.
- `test-release-*` — checksum/provenance and final release-readiness gates.

CI workflows under `.github/workflows/` call these scripts directly. Live gates are operator-driven and documented in `TESTING-2.6.md`.
