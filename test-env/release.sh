#!/usr/bin/env bash
# Publishes a test release into dist/ for the self-update E2E, made from the bundle scripts/package.sh built (no
# rebuild): the same binaries with a new version number, signed like a real release — or deliberately broken.
#
#   test-env/release.sh <version> [--broken-panel] [--tamper] [--sign-with <key.pem>] [--next-key <public.pem>]
#     --broken-panel   the panel exits at once (ts-update must put the previous version back)
#     --tamper         the channel file is changed after signing (the agent must refuse it)
#     --sign-with      sign with this private key instead of the usual one
#     --next-key       the key the bundle hands over (rotation); default: the signing key's own public half
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
DIST="$ROOT/dist"
VERSION="${1:?Usage: test-env/release.sh <version> [--broken-panel] [--tamper]}"
shift
BROKEN=0
TAMPER=0
SIGN_WITH=""
NEXT_KEY=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --broken-panel) BROKEN=1; shift ;;
        --tamper) TAMPER=1; shift ;;
        --sign-with) SIGN_WITH="$2"; shift 2 ;;
        --next-key) NEXT_KEY="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# shellcheck source=../scripts/release-key.sh
source "$ROOT/scripts/release-key.sh"
resolve_signing_key "$ROOT"
[[ -z "$SIGN_WITH" ]] || SIGNING_KEY="$SIGN_WITH"
BUNDLE_KEY="$(mktemp)"
if [[ -n "$NEXT_KEY" ]]; then cp "$NEXT_KEY" "$BUNDLE_KEY"; else public_key > "$BUNDLE_KEY"; fi

# The bundle package.sh built is the base of every test release.
if [[ ! -f "$DIST/base-bundle" ]]; then
    find "$DIST/files" -name '*-linux-x64.tar.gz' -printf '%f\n' | head -n1 > "$DIST/base-bundle"
fi
BASE="$DIST/files/$(cat "$DIST/base-bundle")"
FILE="ts-$VERSION-linux-x64.tar.gz"

# Python's tarfile keeps every entry's mode and owner (re-packing on Windows would lose the executable bits).
PYTHON=""
for candidate in python3 python; do
    if "$candidate" -c 'import tarfile' >/dev/null 2>&1; then PYTHON="$candidate"; break; fi
done
[[ -n "$PYTHON" ]] || { echo "Python 3 is needed to re-pack the bundle." >&2; exit 1; }

"$PYTHON" - "$BASE" "$DIST/files/$FILE" "$VERSION" "$BROKEN" "$BUNDLE_KEY" <<'PYTHON_SCRIPT'
import io, sys, tarfile
source, target, version, broken, key = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4] == "1", sys.argv[5]
replace = {"VERSION": (version + "\n").encode(), "release-key.pem": open(key, "rb").read().replace(b"\r\n", b"\n")}
if broken:
    replace["panel/ts-panel"] = b"#!/bin/sh\necho 'this test release is broken on purpose' >&2\nexit 1\n"
with tarfile.open(source, "r:gz") as src, tarfile.open(target, "w:gz") as dst:
    for member in src.getmembers():
        name = member.name[2:] if member.name.startswith("./") else member.name
        if name in replace:
            data = replace[name]
            member.size = len(data)
            dst.addfile(member, io.BytesIO(data))
        else:
            dst.addfile(member, src.extractfile(member) if member.isfile() else None)
PYTHON_SCRIPT

SHA="$(sha256sum "$DIST/files/$FILE" | awk '{print $1}')"
NOTES="Test release $VERSION$([[ $BROKEN -eq 1 ]] && echo ' (broken on purpose)' || true)."
write_channel "$DIST/channels/stable.json" "$VERSION" stable "{\"x64\":{\"url\":\"files/$FILE\",\"sha256\":\"$SHA\"}}" "$NOTES"
sign_file "$DIST/channels/stable.json"

if [[ $TAMPER -eq 1 ]]; then
    sed -i 's/"notes":"[^"]*"/"notes":"Changed after signing."/' "$DIST/channels/stable.json"
fi

echo "==> Published test release $VERSION$([[ $BROKEN -eq 1 ]] && echo ' (broken panel)' || true)$([[ $TAMPER -eq 1 ]] && echo ' (tampered channel file)' || true)"
