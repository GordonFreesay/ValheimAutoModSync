# Third-Party Notices

AutoModSync-authored code is licensed under the included `LICENSE` (MIT).

The Thunderstore package does not bundle the BepInEx runtime or Valheim/Unity game assemblies. BepInEx is installed separately through the declared Thunderstore dependency:

- denikson-BepInExPack_Valheim 5.4.2350

AutoModSync is compiled against BepInEx, Harmony, Valheim, and Unity APIs, but those third-party runtime/game binaries are not redistributed inside the Thunderstore package.

Relevant upstream projects retain their own licenses and copyrights:

- BepInEx — MIT
- Harmony / HarmonyX — MIT
- Valheim — Iron Gate AB / Coffee Stain Publishing
- Unity — Unity Technologies

## Operator-served third-party mods

This package notice covers AutoModSync and its declared/runtime dependencies. It does not grant redistribution rights for unrelated gameplay mods that a server operator chooses to synchronize through AutoModSync.

Server operators are responsible for confirming that each selected mod's license or author permissions allow redistribution to connecting clients and for complying with any conditions. A private/password-protected or noncommercial server does not by itself create redistribution permission.

Policy: https://github.com/GordonFreesay/ValheimAutoModSync/blob/main/THIRD-PARTY-MOD-REDISTRIBUTION.md
