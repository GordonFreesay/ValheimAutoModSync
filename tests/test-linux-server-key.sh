#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

command -v mcs >/dev/null 2>&1
mcs -nologo -sdk:4.7.2 -langversion:5 -out:"$tmp/LinuxServerKeyHarness.exe" \
    "$root/Source/AutoModSync.PrivateKeySecurity.cs" \
    "$root/tests/LinuxServerKeyHarness.cs"
mono "$tmp/LinuxServerKeyHarness.exe"
