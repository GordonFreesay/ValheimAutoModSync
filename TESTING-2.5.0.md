# AutoModSync 2.5.0 test checklist

## 1. Build the development release

From a Windows machine with Valheim installed:

```powershell
git checkout ai/2.5.0-compat-signing
git pull
.\verify-source-docs.ps1
.\build-release.bat
```

The standalone package should be:

```text
Dist\ValheimAutoModSync-2.5.0.zip
```

The extracted package must contain:

```text
ValheimAutoModSyncInstaller.exe
install.bat
Client\...
Server\...
Tools\...
Bundled\BepInExPack_Valheim-5.4.2350.zip
```

The GUI installer is the primary path. `install.bat` is the manual fallback.

Unsigned development builds can still receive Windows SmartScreen/reputation warnings. The signing-required release path is tested separately after a trusted signing identity is configured.

## 2. Installer test

Start from a client with no third-party plugins.

Keep/remove as appropriate:

- It is safe to remove the existing `BepInEx` folder, `winhttp.dll`, and `doorstop_config.ini` for a true clean-install test.
- Do not delete Valheim game files.
- Close Valheim before installing.

Run:

```text
ValheimAutoModSyncInstaller.exe
```

Choose **Client** and confirm the detected Valheim folder.

Expected result:

```text
Valheim\
  winhttp.dll
  doorstop_config.ini
  BepInEx\
    core\...
    plugins\ValheimAutoModSync.Client.dll
    AutoModSync\ValheimAutoModSync.Apply.exe
```

No network download should occur during installer execution.

## 3. Bare-client compatibility test

Leave the server populated with the real mod set, including the combinations that reproduced the 2.4.8 problem (for example Jotunn, Epic Loot, JsonDotNET, and BuildRestrictionTweaksSync).

On the client, begin with only the 2.5.0 AutoModSync client runtime installed.

Before connecting, the BepInEx log should show AutoModSync but not the server's synchronized third-party plugins.

Connect normally.

Expected first-connection sequence includes:

```text
AutoModSync held Valheim ServerHandshake until preflight completes.
AutoModSync preflight probe sent before Valheim ServerHandshake.
AutoModSync preflight acknowledged by server 2.5.0.
AutoModSync server detected; checking required mods before joining.
```

When a current 2.5 server and client are both present, a large synchronization should log:

```text
AutoModSync server supports binary batched bundle transfer.
AutoModSync using pipelined binary bundle transfer (up to 128 chunks requested per window; 16 chunks per Steam message).
```

On a Steam dedicated-server connection, the server should additionally log the live per-connection values, for example:

```text
AutoModSync Steam bundle transport: SendRateMin 153600 -> 8388608, SendRateMax 153600 -> 33554432, SendBuffer <old> -> 16777216 B.
```

At transfer cleanup it should log that the previous Steam bundle transport settings were restored. If the transport line says `n/a`, names a non-Steam socket, or reports a lower value than requested, preserve that log: it identifies which Steam/transport setting refused the live override.

While a longer transfer is active, the server also emits low-rate live Steam telemetry (at most once every five seconds), for example:

```text
AutoModSync Steam transfer telemetry: rate=..., pendingReliable=... B, unackedReliable=... B, ping=... ms.
```

This line distinguishes an AutoModSync framing problem from Steam's own bandwidth estimator or reliable queue remaining pinned.

A prior live run reported `rate=1048576 B/s` on every sample after the old 1 MiB/s minimum was applied. That result means the Steam estimator was not climbing above the floor during the short sync; current development defaults intentionally raise the temporary floor to 8 MiB/s. The expected next test should therefore report a live rate materially above 1 MiB/s.

After verification, the client also logs the measured transfer size, elapsed time, and MiB/s.

If binary batching is unavailable, transfer falls back to `bundle-window1`, then to the original one-chunk AMS4 behavior.

If files are missing, AutoModSync should request, verify, stage, restart, and reconnect before Jotunn or another compatibility framework can reject the incomplete client.

### Multi-root compatibility checks

Test at least one real or fixture file in each supported root:

```text
BepInEx/plugins   -> manifest kind P
BepInEx/patchers  -> manifest kind R
BepInEx/config    -> manifest kind C (only when SyncConfigPatterns matches)
```

For the patcher test, remove the required patcher from the client, connect, let AutoModSync restart, and verify the file is physically under `BepInEx/patchers` **before** the new BepInEx boot completes. It must never be written under `plugins`.

For the config test, first leave `SyncConfigPatterns` empty and verify the server config is not advertised. Then add one exact/controlled pattern and verify only that file is synchronized to `BepInEx/config`. A broad pattern must still never synchronize `ValheimAutoModSync.private.xml`, `ValheimAutoModSync.public.xml`, or `BepInEx.cfg`.

For side-classification, place a harmless server-only fixture under plugins/patchers, match it with `ServerOnlyPatterns`, and verify it does not appear in the client manifest. Then test `ClientRequiredPatterns` with a folder wildcard and verify only the selected client-required subtree is advertised.

A 2.4.x client may still interoperate with a plugin-only 2.5 server. If the server manifest contains patcher/config roots, it should require the 2.5 `roots1` capability instead of silently treating those entries as plugins.

## 4. Second-boot/reconnect test

After the synchronized restart, BepInEx should load the newly synchronized dependency set before the reconnect reaches mod compatibility checks.

Expected AutoModSync sequence:

```text
AutoModSync preflight acknowledged by server 2.5.0.
AutoModSync: client mods already match the server.
AutoModSync released the original Valheim ServerHandshake with <N> argument(s) after preflight.
```

The post-sync reconnect must proceed beyond this point without a server-side `EndOfStreamException in ZRpc::HandlePackage`. That exception is a regression indicator that the held vanilla handshake was not replayed byte-for-byte/argument-for-argument correctly.

After that point, Jotunn/other frameworks should perform their normal checks. AutoModSync does not suppress their result.

## 5. Non-AutoModSync server test

Connect the 2.5.0 client to a server that does not run AutoModSync.

Expected behavior:

- AutoModSync briefly holds the vanilla ServerHandshake.
- No AMS response is received.
- After the discovery timeout, AutoModSync releases the normal handshake.
- No files are modified.

## 6. Installer preservation tests

Verify separately:

- Existing BepInEx is preserved.
- Existing AutoModSync server config is preserved.
- Existing server private signing identity is preserved.
- Unknown `winhttp.dll` is not overwritten when BepInEx is absent.
- Unknown `version.dll` is preserved.
- The one known legacy AutoModSync 2.4.4 packed `version.dll` is removed only when its SHA-256 matches the historical known hash.
- Dedicated-server BepInEx installation uses `Bundled\BepInExPack_Valheim-5.4.2350.zip` and rejects a hash mismatch.

## 7. Signed-release test (later)

Once a trusted signing identity is configured:

```text
build-signed-release.bat
```

That build must fail if any AutoModSync-authored PE file cannot be signed or cannot pass Authenticode verification, including `ValheimAutoModSyncInstaller.exe`.
