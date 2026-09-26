# AutoModSync 2.6.1 patch-release plan

This document is the planning baseline for Valheim AutoModSync **2.6.1**.

2.6.1 is intended to be a focused documentation/safety/release-pipeline patch over 2.6.0. It must not silently replace, mutate, or republish the already-attested 2.6.0 release artifact.

## Baseline and release invariants

- Public release being patched: **2.6.0**
- 2.6.0 release tag commit: `0da61e625220766aca6c47bd990e063d000d3bea`
- Planning baseline on `main`: `a149aa74942cff0729bedfba3e6685ed6c1ec61f`
- Planned release: **2.6.1**
- Network protocol baseline remains **AMS4 / protocol 4**.
- The existing `v2.6.0` tag, GitHub release asset, checksum, and attestation are immutable historical artifacts. Do **not** overwrite the 2.6.0 ZIP or move/reuse the `v2.6.0` tag.
- 2.6.1 must be built, hashed, attested, and published as new exact artifacts under a new `v2.6.1` tag.
- No public 2.6.1 release is authorized until every required gate in this document has passed on the exact release candidate.

## Required 2.6.1 scope

### 1. Ship the third-party redistribution policy inside every package

The repository now documents operator responsibility in:

- `THIRD-PARTY-MOD-REDISTRIBUTION.md`
- `README.md`
- `THIRD-PARTY-NOTICES.md`
- `ModSites/README.md`
- `Thunderstore/README.md`
- `Thunderstore/THIRD-PARTY-NOTICES.md`
- `Server/server-config-example.cfg`

2.6.1 must make sure newly downloaded packages actually contain the warning, not merely that the repository contains it.

Required packaging behavior:

- Standalone ZIP includes `THIRD-PARTY-MOD-REDISTRIBUTION.md` at the package root in addition to the updated README and third-party notices.
- Nexus and CurseForge ZIPs include the same policy at the package root in addition to their updated README/notices.
- Thunderstore/r2modman ZIP includes the same policy at the package root in addition to its updated README/notices.
- Package generation fails if the policy file is absent.
- A release regression test opens all four ZIPs and verifies the policy file and key operator-responsibility language are present.
- Release notes explicitly state that AutoModSync grants no redistribution rights over operator-selected third-party mods.

The warning must retain these points:

1. AutoModSync is a transport/synchronization tool and does not grant or expand rights to third-party mods.
2. Operators are responsible for checking applicable licenses/author permissions and satisfying their conditions.
3. Private, password-protected, friends-only, or noncommercial use does not by itself create redistribution permission.
4. Public servers deserve particular review because recipients may be unrestricted.
5. AutoModSync itself does not bundle arbitrary third-party gameplay mods or fetch them from mod repositories on behalf of the operator.

### 2. Remove forced Valheim termination from the Apply helper

Current 2.6.0 behavior waits for the originating Valheim PID and, after 15 seconds, can call `Process.Kill()`.

2.6.1 must remove that forced-termination behavior.

Required behavior:

- The helper may wait a bounded amount of time for the exact originating PID to exit naturally.
- If the process remains alive past that wait, the helper must abort **before any live synchronized file mutation**.
- It must not terminate Valheim, Steam, or any unrelated process.
- Verified staging/pending state must remain recoverable for a later retry.
- The failure must be clearly recorded in the apply log.
- A subsequent clean retry after the old process exits must still be able to complete the existing transactional apply/recovery path.

Required regression:

- Start the helper against a deliberately long-lived dummy PID.
- Prove the dummy process remains alive.
- Prove no synchronized live file changes occurred.
- Prove pending/staging state remains usable.
- Exit the dummy process and prove a later helper invocation completes successfully.

This is an AV-hygiene and user-safety improvement. It must **not** be described as a guarantee that Defender/SmartScreen/other AV products will stop flagging the helper.

### 3. Fix stale first-contact trust dialogs after connection failure/timeout

Observed 2.6.0 regression: if the user leaves the first-contact fingerprint/security-code prompt unanswered long enough for the protected connection attempt to fail, the native AutoModSync trust dialog can remain visible after the connection is gone.

2.6.1 must bind native trust-prompt lifetime to the exact connection/trust generation that created it.

Required behavior:

- When that protected connection is invalidated, its trust prompt is dismissed reliably.
- A late Yes/No result from an invalidated prompt can never establish trust or resume a later connection.
- A later connection uses a new generation and, when appropriate, presents a fresh prompt.
- Prompt cleanup must not block Unity networking or the main thread.

Required regression:

- Reproduce the actual user-visible case: open first-contact trust prompt, do not answer it, allow the connection/preflight to fail or time out, and verify the native dialog disappears.
- Verify late/stale prompt results are ignored.
- Verify a subsequent connection can prompt and proceed normally.
- Extend the existing stale-trust live harness rather than relying only on a synthetic path that does not reproduce the reported timeout lifecycle.

Do not redesign the entire trust UX in this patch unless the minimal reliable fix requires it.

### 4. Promote canonical store artifacts instead of recompiling during publication

The tag-triggered `Distribution Packages` workflow already builds standalone/Nexus/CurseForge/Thunderstore packages from one compilation. The manual per-store publishing workflows currently compile again.

2.6.1 must make **single-build promotion** the release invariant.

Required behavior:

- `Distribution Packages` remains the canonical compiler/package producer for a release tag.
- `publish-nexus.yml`, `publish-curseforge.yml`, and `publish-thunderstore.yml` do not compile AutoModSync PE files.
- Each publishing workflow resolves the successful canonical `Distribution Packages` run for the requested release tag/version and downloads the exact store ZIP plus the canonical `SHA256SUMS.txt`.
- Publication fails if the artifact name, version, tag/commit association, or SHA-256 does not match the canonical manifest.
- The already-generated GitHub/Sigstore provenance remains tied to the exact promoted artifact bytes.
- The publishing workflow uploads those exact bytes to the store without repackaging them.

Add a regression gate that extracts all four canonical distribution ZIPs and proves that the AutoModSync-authored runtime binaries are byte-identical across applicable channels:

- `ValheimAutoModSync.Client.dll`
- `ValheimAutoModSync.Server.dll`
- `ValheimAutoModSync.Apply.exe`

Deterministic compiler output may still be investigated later, but it is not a substitute for this single-build promotion rule.

### 5. Make release-readiness/version machinery patch-release safe

Current release-readiness checks contain 2.6.0-specific constants.

For 2.6.1:

- Bump the authoritative `VERSION` to `2.6.1` only when implementation work begins.
- Update client/server/apply/installer/build-tool assembly/product metadata consistently.
- Update Thunderstore package metadata/changelog.
- Create `RELEASE-NOTES-2.6.1.md`.
- Update README/distribution text at release freeze.
- Refactor release-readiness checks where practical to derive the current version from `VERSION` rather than hard-coding `2.6.0`.
- Release-readiness must require the correct version-specific release-notes file and distribution filenames.
- Preserve the existing rule that development-only `AMS_DEV_TESTS` code cannot enter release binaries.

### 6. Final exact-candidate Microsoft Defender gate

Because 2.6.0's Apply helper received generic Defender ML classifications, 2.6.1 must test the **exact canonical candidate bytes**, not a locally rebuilt approximation. This is the final release gate after the candidate is frozen, tagged, built, hashed, and attested, but before public publication.

Microsoft's software-developer submission channel is an analysis/false-positive process, not a pre-certification, permanent allowlist, or release-signing service. Therefore the release gate is conditional on the exact candidate's Defender result rather than pretending every version can receive formal Microsoft "approval."

Required final sequence:

1. Freeze the exact 2.6.1 release candidate. No source/runtime/package changes after this point without restarting the gate.
2. Tag `v2.6.1` and let the canonical `Distribution Packages` workflow produce the exact standalone and store artifacts.
3. Download the exact canonical standalone ZIP and extract the exact canonical `ValheimAutoModSync.Apply.exe`.
4. Verify the ZIP/checksum manifest with SHA-256 and GitHub/Sigstore attestation.
5. Update Microsoft Defender Security Intelligence to the latest available definitions and make sure no local allow/exclusion is masking the test.
6. Fresh-download or otherwise present the exact canonical bytes to Defender and record:
   - Defender definition version
   - ZIP SHA-256
   - Apply-helper SHA-256
   - any detection name/classification
   - date/time of the test
7. **If either exact artifact is detected or blocked:** submit the exact affected ZIP and/or Apply helper through Microsoft's **Software developer** file-submission path, record every Submission ID, and hold public release until Microsoft returns a final determination or corrective update sufficient for a fresh current-definition retest.
8. After Microsoft closes a detected-file submission as clean/false positive, update Defender definitions again and repeat a fresh exact-byte test with no local allow-rule. Release only after the exact candidate no longer reproduces the Defender malware block, or after an explicitly documented maintainer decision to ship despite an unresolved Microsoft classification.
9. **If the exact canonical candidate is not detected:** record the clean current-definition result and proceed. A proactive Microsoft submission may be made for additional analysis, but it is not treated as certification and is not required to delay release when there is no detection to dispute.
10. Do not tell users to disable Defender, add a permanent exclusion, or blindly allow the file as a normal installation step.

If an unresolved Defender classification remains and the maintainer deliberately chooses to publish anyway, the release page, website, and store listings must carry a conspicuous current-status notice identifying the exact affected version and explaining that the file is under Microsoft review. Do **not** use a permanent boilerplate warning on every clean release; the notice is only for a release that is actually shipping with a known unresolved classification.

A clean Defender result or Microsoft false-positive correction is distribution evidence, not a substitute for code review, provenance, or Authenticode.

## Release packaging gates

Create or extend tests so 2.6.1 cannot ship unless all of the following are true:

| Gate | Required evidence |
| --- | --- |
| Redistribution docs | All four release ZIPs contain the policy file and updated warning text |
| No forced kill | Production Apply source/binary path contains no Valheim `Process.Kill()` fallback and long-lived-PID regression passes |
| Trust-dialog lifecycle | Real timeout/disconnect case closes the native prompt and stale results cannot affect another connection |
| Transaction safety | Existing Phase 2/6 apply/ownership regression suites remain green |
| Transfer compatibility | Existing AMS4 resume/scheduler/cache/ownership gates remain green |
| Binary identity | Client/Server/Apply hashes match across all applicable canonical channel packages |
| Canonical promotion | Store workflows consume canonical Distribution Packages artifacts and perform no compilation/repackaging |
| Version consistency | `VERSION`, assembly metadata, package metadata, docs, and release notes all resolve to 2.6.1 |
| Provenance | Canonical packages and checksum manifest have valid GitHub/Sigstore attestations |
| Defender/Microsoft final gate | Exact canonical candidate tested with current definitions; any reproduced detection submitted through the Software developer channel and resolved/retested before normal publication |

## Recommended implementation order

1. **Planning baseline only** — this document; do not bump version yet.
2. **Apply-helper safety fix** — remove forced kill and add regression.
3. **Trust-dialog timeout fix** — reproduce first, then make the smallest lifecycle correction and extend live regression.
4. **Package-policy inclusion** — update builders and add ZIP-content regression.
5. **Single-build store promotion** — change publishing workflows and add binary-identity/promotion gate.
6. **2.6.1 version/release metadata** — bump version and create release notes once implementation is stable.
7. **Full CI + targeted live tests** — rerun existing safety/transfer/apply gates plus the new 2.6.1 gates.
8. **Release freeze** — no runtime changes after the candidate used for final qualification.
9. **Tag `v2.6.1`** — canonical Distribution Packages workflow builds the exact release bytes.
10. **Verify/download/test exact tagged artifacts** — checksum, attestation, package contents, binary identity, and the final Microsoft Defender gate. If Defender reproduces a detection, submit the exact affected artifact(s) to Microsoft as a Software developer and wait for final determination/corrective definitions before normal publication.
11. **Publish GitHub release** from the exact canonical artifact bytes only after the final Defender/Microsoft gate is satisfied or an unresolved-classification exception is explicitly documented.
12. **Promote the exact canonical Nexus/CurseForge/Thunderstore artifacts**; do not rebuild.
13. **Update website/store descriptions** to 2.6.1 and retain the third-party redistribution warning.
14. Keep 2.6.0 available as historical provenance unless there is a separate reason to withdraw it; do not mutate its artifact.

## Explicit non-goals for 2.6.1

Keep this patch small enough to qualify quickly.

The following are useful ideas but should not be allowed to expand 2.6.1 unless a blocker proves they are necessary:

- New network protocol generation.
- New transfer algorithms or throughput tuning.
- Broad UI redesign.
- Arbitrary new synchronization roots.
- Automatic license detection.
- A new "required but verify-only / do-not-transfer" third-party-mod feature.

The verify-only concept is worth designing separately because it could let an operator require a locally installed mod without redistributing its bytes, but it introduces product/UX/configuration semantics that deserve their own reviewed workstream rather than being rushed into this safety patch.

## Release-note headline

The eventual 2.6.1 release notes should present the patch approximately as:

> **AutoModSync 2.6.1 is a safety, documentation, and release-integrity patch.** It adds explicit third-party mod redistribution guidance to shipped packages, removes the Apply helper's forced Valheim termination fallback, fixes stale first-contact trust prompts after failed connections, and changes store publication to promote the exact canonical GitHub-built artifacts instead of recompiling them.

Do not claim that the AV change guarantees a clean malware scan, and do not imply that AutoModSync itself grants permission to redistribute third-party mods.
