#!/usr/bin/env bash
# Build per-platform Qora VS Code extensions.
#
# For each target: publish the self-contained, single-file Qora CLI (NO trim / NO AOT — Janglim's
# grammar reflection breaks under both) into bin/<target>/, then `vsce package --target <target>`
# so each .vsix carries exactly one platform's ~70 MB binary. Output goes to dist/.
#
# Usage:
#   scripts/build-vsix.sh                 # all supported targets
#   scripts/build-vsix.sh win32-x64       # just one (or a subset)
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"            # qora-vscode/
qora_csproj="$here/../src/Qora/Qora.csproj"

# vsce target (= process.platform-process.arch, the folder the extension looks in)  ->  dotnet RID
targets_all=(win32-x64 darwin-arm64 linux-x64)
rid_for() {
  case "$1" in
    win32-x64)    echo win-x64 ;;
    darwin-arm64) echo osx-arm64 ;;
    linux-x64)    echo linux-x64 ;;
    *) echo "" ;;
  esac
}

targets=("$@"); [ ${#targets[@]} -eq 0 ] && targets=("${targets_all[@]}")

ver="$(grep -m1 '"version"' "$here/package.json" | sed -E 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"

rm -rf "$here/dist"; mkdir -p "$here/dist"
for t in "${targets[@]}"; do
  rid="$(rid_for "$t")"
  [ -z "$rid" ] && { echo "unknown target: $t (expected one of: ${targets_all[*]})"; exit 1; }
  echo ">>> $t  (dotnet RID: $rid)"

  rm -rf "$here/bin"                                # only the current target's binary is present at pack time
  dotnet publish "$qora_csproj" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -o "$here/bin/$t" --nologo
  rm -f "$here/bin/$t"/*.pdb                        # debug symbols aren't needed to ship
  [ "$t" = win32-x64 ] || chmod +x "$here/bin/$t/Qora"   # set exec bit for unix targets at pack time

  npx --yes @vscode/vsce package --target "$t" --allow-missing-repository \
    -o "$here/dist/qora-$t-$ver.vsix"
done

rm -rf "$here/bin"
echo "done -> $here/dist"
ls -la "$here/dist"
