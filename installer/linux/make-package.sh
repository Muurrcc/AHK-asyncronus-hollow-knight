#!/usr/bin/env bash
# Builds dist/Asyncronus-linux-X.Y.Z.tar.gz for Linux users.
# Requires: .NET SDK 8+, tar, and installer/build/payload already produced by installer/build.ps1
# (payload.ini, Assembly-CSharp.patch, api/, mod/, tools/). Run build.ps1 once (Windows) or copy that folder.
#
#   ./make-package.sh [--payload DIR] [--out DIR]
#
# Guarantees checked on the FINAL archive (it is extracted and inspected before the script reports OK):
#   * install.sh / uninstall.sh / lib.sh have LF line endings (CRLF => "bash\r: No such file or directory" on Linux)
#   * install.sh / uninstall.sh are executable (+x does not survive Windows / Git Bash by itself)
#   * every file the installer needs is inside (delta tool, payload.ini, patch, HookGen tools, mod)
set -euo pipefail

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
INSTALLER=$(cd -- "$HERE/.." && pwd)
PAYLOAD="$INSTALLER/build/payload"
OUT="$INSTALLER/../dist"
CR=$'\r'
# GNU tar reads "C:/..." as host:file; --force-local avoids that with Windows paths (Git Bash).
TAR_OPTS=(); if tar --help 2>&1 | grep -q -- --force-local; then TAR_OPTS+=(--force-local); fi
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
# the payload exactly as the MSI ships it (patch, third-party libs, mod, HookGen tools). No game code inside.
cp -R "$PAYLOAD/." "$ROOT/payload/"
cp "$INSTALLER/../LICENSE" "$ROOT/" 2>/dev/null || true
cp "$INSTALLER/../docs/LINUX.md" "$ROOT/README-LINUX.md" 2>/dev/null || true

# Text files: LF only (the scripts would not even start with CRLF on Linux).
for f in install.sh uninstall.sh lib.sh README-LINUX.md payload/payload.ini tools/AsyncronusDelta.runtimeconfig.json; do
    [ -f "$ROOT/$f" ] && sed -i "s/$CR\$//" "$ROOT/$f"
done

# Permissions do not survive Windows / Git Bash: set them explicitly (dirs and the two entry scripts 755, the rest 644).
find "$ROOT" -type d -exec chmod 755 {} +
find "$ROOT" -type f -exec chmod 644 {} +
chmod 755 "$ROOT/install.sh" "$ROOT/uninstall.sh"

mkdir -p "$OUT"
PKG="$OUT/Asyncronus-linux-$VERSION.tar.gz"
# --owner/--group: do not leak the builder's Windows uid/name into the archive.
tar "${TAR_OPTS[@]}" -C "$STAGE" --owner=0 --group=0 --numeric-owner -czf "$PKG" "Asyncronus-linux-$VERSION"

# ---- verify the archive that will actually be published
CHECK=$(mktemp -d "${TMPDIR:-/tmp}/asy-check.XXXXXXXX")
tar "${TAR_OPTS[@]}" -xzf "$PKG" -C "$CHECK"
CHK="$CHECK/Asyncronus-linux-$VERSION"
bad=0
for f in install.sh uninstall.sh lib.sh; do
    if grep -q "$CR" "$CHK/$f"; then echo "ERROR: $f has CRLF line endings in the archive" >&2; bad=1; fi
    bash -n "$CHK/$f" || { echo "ERROR: $f has a syntax error" >&2; bad=1; }
done
for f in install.sh uninstall.sh; do
    tar "${TAR_OPTS[@]}" -tvzf "$PKG" | grep -E "/$f\$" | grep -q '^-rwx' || { echo "ERROR: $f is not executable in the archive" >&2; bad=1; }
done
for f in tools/AsyncronusDelta.dll tools/AsyncronusDelta.runtimeconfig.json payload/payload.ini payload/Assembly-CSharp.patch \
         payload/tools/PrePatcher.exe payload/tools/MonoMod.RuntimeDetour.HookGen.exe payload/tools/Mono.Cecil.dll \
         payload/mod/HKMP.dll; do
    [ -s "$CHK/$f" ] || { echo "ERROR: $f is missing or empty in the archive" >&2; bad=1; }
done
[ -n "$(ls -A "$CHK/payload/api" 2>/dev/null)" ] || { echo "ERROR: payload/api is empty" >&2; bad=1; }
rm -rf -- "$CHECK"
[ "$bad" -eq 0 ] || { rm -f -- "$PKG"; exit 1; }
echo "OK: $PKG"
