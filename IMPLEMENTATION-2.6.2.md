# AutoModSync 2.6.2+ planning backlog

This document collects work intentionally deferred from the narrow **2.6.1 password access-control patch**.

2.6.1 is reserved for one runtime change only: password-protected servers must not expose protected AutoModSync metadata or transfer synchronized content until Valheim has accepted the correct server password for that exact connection.

The items below begin with **2.6.2** and may be split across later patch/minor releases if that produces safer, easier-to-review releases.

## Candidate 2.6.2 safety/release-integrity work

### Apply-helper process safety

Remove the 2.6.0 forced Valheim termination fallback from `ValheimAutoModSync.Apply.exe`.

Target behavior:

- wait a bounded amount of time for the originating Valheim PID to exit naturally;
- if it remains alive, abort before mutating synchronized live files;
- never terminate Valheim, Steam, or unrelated processes;
- preserve verified staging/pending state for a later retry;
- prove a later clean invocation can complete the existing transactional apply path.

### Stale first-contact trust-dialog lifecycle

Fix the known case where a first-contact trust/security-code prompt can remain visible after its protected connection has already failed or timed out.

Target behavior:

- prompt lifetime is tied to the exact connection/trust generation that created it;
- invalidating the connection dismisses the prompt;
- late Yes/No results from stale prompts are ignored;
- later connections use a fresh generation and can prompt normally.

### Ship redistribution guidance inside release packages

Ensure standalone, Nexus, CurseForge, and Thunderstore/r2modman packages carry the project redistribution policy/notices, and add package-content regression checks.

This remains documentation/compliance guidance only; AutoModSync does not decide that a third-party license grants redistribution rights.

### Canonical single-build store promotion

Make the tag-triggered Distribution Packages workflow the canonical producer for all release channels.

Target behavior:

- store publishing workflows consume the exact canonical Nexus/CurseForge/Thunderstore ZIP produced for the tagged release;
- publishing workflows do not recompile or repackage AMS binaries;
- hashes/version/tag association must match the canonical manifest;
- Client/Server/Apply authored binaries are byte-identical across applicable channel packages.

### Patch-release/readiness cleanup

Refactor version/release-readiness checks so future patch releases derive version-specific expectations from `VERSION` where practical instead of embedding old release constants.

### Exact-candidate Defender/Microsoft qualification

Continue improving the release process around exact canonical candidate bytes:

- scan the exact tagged/downloaded artifacts with current Defender definitions;
- record candidate hashes and definition version;
- if a candidate reproduces a Defender detection, submit those exact bytes through Microsoft's software-developer path and retest after Microsoft's response/current definitions;
- never instruct users to disable Defender as a normal install step.

This is distribution evidence, not a substitute for code review, provenance, or Authenticode.

## 2.6.2+ licensing/acquisition design

### Required but verify-only / do-not-transfer mods

Add a requirement mode that lets a server require a locally installed mod without AutoModSync distributing its bytes.

Concept:

- server manifest can declare a requirement as **verify-only**;
- AMS compares local identity/version/hash;
- missing or incorrect requirements block the protected join;
- those files are never included in a server transfer bundle;
- server can provide descriptive/provider metadata needed to help the user obtain the mod from an authorized source.

This is the primary product mechanism for mods whose redistribution rights are absent, unclear, or intentionally not exercised by the server operator.

### Authorized-source handoff UX

Design a native/browser acquisition screen for missing verify-only requirements.

Possible actions:

- open the mod's official Nexus/Thunderstore/CurseForge/source page;
- use provider-native package-manager/deep-link mechanisms where officially supported;
- copy the missing-mod list;
- open the local mod folder;
- recheck installed files after the user obtains them.

Security boundary:

- AMS should prefer provider identifiers and trusted-provider templates over arbitrary server-supplied executable/download URLs;
- actual mod bytes should flow from the authorized provider to the user, not through the AutoModSync server;
- no automatic license inference.

## Later work

Potential later work includes:

- deterministic/reproducible builds;
- revisiting trusted Authenticode signing;
- operator observability/reporting;
- public-demo soak/stress qualification;
- Valheim-update compatibility tooling;
- Linux/Steam Deck architecture;
- transfer/protocol changes only when real measurements justify them.

## Release-splitting rule

Do not force every item in this file into 2.6.2.

Prefer small, auditable releases. If 2.6.2 becomes broad, split independent work into 2.6.3 or a later minor release instead of delaying unrelated fixes.
