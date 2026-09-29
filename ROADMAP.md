# AutoModSync roadmap

AutoModSync **2.6.1 is feature-complete for the Windows 2.x product line**.

## 2.x maintenance mode

No additional 2.x product features are planned.

Future 2.x releases, if needed, are limited to maintenance:

- bug fixes;
- security fixes;
- compatibility fixes required by Valheim, BepInEx, Steam networking, or supported distribution channels;
- release/build/provenance corrections needed to keep existing functionality shippable and verifiable; and
- antivirus false-positive/reputation follow-up that does not expand product behavior.

Previously discussed feature ideas such as provider-assisted mod acquisition, verify-only requirements, license inference, new synchronization roots, new transfer/protocol features, broad UI expansion, operator dashboards, or similar scope expansion are **not on the roadmap**.

## Product boundary

AutoModSync remains a server-driven synchronization and integrity tool.

The server operator chooses which files the server is configured to synchronize and is responsible for ensuring those files may be redistributed. AutoModSync does not determine, grant, certify, or infer third-party redistribution rights and does not acquire third-party mods from external providers on the operator's behalf.

Password-protected servers use the 2.6.1 server-access boundary so protected synchronization metadata and payloads are not disclosed before normal Valheim server access is granted. This provides privacy/access control for a private mod environment; it does not itself grant redistribution rights.

## Possible 3.0

The only contemplated future feature line is a **Linux / Steam Deck port under 3.0**.

3.0 is **not scheduled or promised**. No Linux validation environment is currently maintained for the project, so the port may never be developed. Windows 2.x remains the supported product unless that changes explicitly in the future.
