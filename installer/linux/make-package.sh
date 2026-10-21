#!/usr/bin/env bash
# Builds dist/Asyncronus-linux-X.Y.Z.tar.gz for Linux users.
# Requires: .NET SDK 8+, tar, and installer/build/payload already produced by installer/build.ps1
# (payload.ini, Assembly-CSharp.patch, api/, mod/, tools/). Run build.ps1 once (Windows) or copy that folder.
#
#   ./make-package.sh [--payload DIR] [--out DIR]
set -euo pipefail

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
INSTALLER=$(cd -- "$HERE/.." && pwd)
PAYLOAD="$INSTALLER/build/payload"
OUT="$INSTALLER/../dist"
while [ $# -gt 0 ]; do
    case "$1" in
        --payload) PAYLOAD=$2; shift 2 ;;
        --out) OUT=$2; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done
[ -f "$PAYLOAD/payload.ini" ] || { echo "No payload.ini in $PAYLOAD (run installer/build.ps1 first)" >&2; exit 1; }
VERSION=$(sed -n 's/^ModVersion=//p' "$PAYLOAD/payload.ini" | tr -d '\r')
[ -n "$VERSION" ] || { echo "ModVersion missing in payload.ini" >&2; exit 1; }

STAGE=$(mktemp -d "${TMPDIR:-/tmp}/asy-pkg.XXXXXXXX")
trap 'rm -rf -- "$STAGE"' EXIT
ROOT="$STAGE/Asyncronus-linux-$VERSION"
mkdir -p "$ROOT/tools" "$ROOT/payload"

dotnet publish "$HERE/delta-tool/AsyncronusDelta.csproj" -c Release -o "$STAGE/delta" -v q -nologo
cp "$STAGE/delta/AsyncronusDelta.dll" "$STAGE/delta/AsyncronusDelta.runtimeconfig.json" "$ROOT/tools/"
cp "$HERE/install.sh" "$HERE/uninstall.sh" "$HERE/lib.sh" "$ROOT/"
chmod +x "$ROOT/install.sh" "$ROOT/uninstall.sh"
# the payload exactly as the MSI ships it (patch, third-party libs, mod, HookGen tools). No game code inside.
cp -R "$PAYLOAD/." "$ROOT/payload/"
cp "$INSTALLER/../LICENSE" "$ROOT/" 2>/dev/null || true
cp "$INSTALLER/../docs/LINUX.md" "$ROOT/README-LINUX.md" 2>/dev/null || true

mkdir -p "$OUT"
tar -C "$STAGE" -czf "$OUT/Asyncronus-linux-$VERSION.tar.gz" "Asyncronus-linux-$VERSION"
echo "OK: $OUT/Asyncronus-linux-$VERSION.tar.gz"
