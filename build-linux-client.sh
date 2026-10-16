#!/bin/sh
# Builds the local-only Linux post-exit apply helper.  The Windows release build
# remains unchanged; a Linux release pipeline must run this on its target ABI.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
out=${1:-"$root/Client/ValheimAutoModSyncLinuxApply"}
mkdir -p "$(dirname -- "$out")"
cc -O2 -Wall -Wextra -std=c11 -o "$out" "$root/Source/ValheimAutoModSync.LinuxApply.c"
chmod 755 "$out"
echo "Built $out"
