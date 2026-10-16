#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
client="$root/Source/ValheimAutoModSync.Client.cs"
server="$root/Source/ValheimAutoModSync.Server.cs"
helper="$root/Source/ValheimAutoModSync.LinuxApply.c"

grep -q 'ValheimAutoModSyncLinuxApply' "$client"
grep -q 'StartLocalApplyHelper(amsRoot)' "$client"
grep -q 'ShowLinuxTrustPrompt' "$client"
grep -q 'Sha256FileWithNormalizedLfNewlines' "$client"
grep -q 'IsWindowsTextSynchronizedFile' "$client"
grep -q 'Windows CRLF-equivalent text file' "$client"
grep -q 'zenity", "kdialog", "yad", "qarma", "xmessage' "$client"
grep -q 'AutoModSync Linux x86_64 server support active' "$server"
grep -q 'ConfigSourcePath' "$server"
grep -q "record.Kind == 'C' ? GetConfigSourceRoot() : Paths.BepInExRootPath" "$server"
grep -q 'ConfigSourcePath must be a physical directory' "$server"
grep -q 'ConfigSourcePath = /config/valheimplus' "$root/Server/server-config-example.cfg"
grep -q 'ManifestBackgroundRefreshLeadSeconds = 60' "$server"
grep -q 'TryRefreshManifestInBackground(now)' "$server"
grep -q 'background manifest refresh ready' "$server"
grep -q 'WriteNewPrivateKeyFile(path, privateXml' "$server"
grep -q 'UseLinuxFileSecurity' "$root/Source/AutoModSync.PrivateKeySecurity.cs"
grep -q 'LinuxNoFollow' "$root/Source/AutoModSync.PrivateKeySecurity.cs"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
cc -O2 -std=c11 -o "$tmp/ValheimAutoModSyncLinuxApply" "$helper"
test -x "$tmp/ValheimAutoModSyncLinuxApply"
echo 'PASS: Linux client routing, desktop trust fallback order, Linux server key routing, and helper compilation.'
