# Third-Party Notices

AutoModSync-authored code is licensed under the root `LICENSE` (MIT). The prebuilt client bootstrap/runtime also carries third-party components supplied through the pinned BepInExPack Valheim runtime. Those components are not relicensed by the AutoModSync MIT license.

The current release builder pins:

- **BepInExPack Valheim 5.4.2350** — https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/
- Package SHA-256 used by AutoModSync: `37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f`

Bundled/runtime components include:

| Component | License | Upstream / source |
| --- | --- | --- |
| BepInEx 5.4.23.5 | MIT | https://github.com/BepInEx/BepInEx/tree/v5.4.23.5 |
| Harmony / HarmonyX | MIT | https://github.com/pardeike/Harmony and https://github.com/BepInEx/HarmonyX |
| MonoMod | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | MIT | https://github.com/jbevain/cecil |
| Unity Doorstop (BepInEx bootstrap component) | GNU LGPL v2.1 | https://github.com/NeighTools/UnityDoorstop |

Copies of the relevant license texts are included under `THIRD_PARTY_LICENSES/`.

AutoModSync does not claim ownership of Valheim, BepInEx, Harmony, MonoMod, Mono.Cecil, Unity Doorstop, Unity, or their trademarks. Valheim game/Unity assemblies used as local build references are not included in this repository.
