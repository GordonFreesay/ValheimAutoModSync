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

If files are missing, AutoModSync should request, verify, stage, restart, and reconnect before Jotunn or another compatibility framework can reject the incomplete client.

## 4. Second-boot/reconnect test

After the synchronized restart, BepInEx should load the newly synchronized dependency set before the reconnect reaches mod compatibility checks.

Expected AutoModSync sequence:

```text
AutoModSync preflight acknowledged by server 2.5.0.
AutoModSync: client mods already match the server.
AutoModSync released Valheim ServerHandshake after preflight.
```

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
