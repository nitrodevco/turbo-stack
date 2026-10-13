#!/usr/bin/env bash
# Builds release bundles the way the install domain serves them:
#
#   dist/install.sh                              (with the release public key embedded)
#   dist/channels/<channel>.json (+ .sig)        version, notes, per-arch download and checksum; signed
#   dist/files/tm-<version>-linux-<arch>.tar.gz
#
# Usage: scripts/package.sh [--arch x64|arm64]... [--channel stable|beta]... [--notes <file>] [--download-base <url>]
#   --channel        each channel file to write (default: stable); a stable release also goes to beta
#   --download-base  absolute address of the bundles (e.g. the GitHub release); default: files/ next to install.sh
#   --docs           also build the documentation site into dist/docs (served from the install domain)
# Environment: VERSION overrides the version from Directory.Build.props; RELEASE_SIGNING_KEY(_FILE) is the signing
# key (see scripts/release-key.sh).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist"
CHANNELS=()
DOWNLOAD_BASE="files"
DOCS=0
NOTES=""
ARCHES=()

# shellcheck source=release-key.sh
source "$ROOT/scripts/release-key.sh"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) ARCHES+=("$2"); shift 2 ;;
        --channel) CHANNELS+=("$2"); shift 2 ;;
        --download-base) DOWNLOAD_BASE="${2%/}"; shift 2 ;;
        --docs) DOCS=1; shift ;;
        --notes) NOTES="$(cat "$2")"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

resolve_signing_key "$ROOT"

[[ ${#ARCHES[@]} -gt 0 ]] || ARCHES=(x64 arm64)
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")}"

[[ ${#CHANNELS[@]} -gt 0 ]] || CHANNELS=(stable)
echo "==> Packaging $VERSION (${CHANNELS[*]}) for ${ARCHES[*]}"
rm -rf "$OUT"
mkdir -p "$OUT/files" "$OUT/channels"

echo "==> Building web UI"
(cd "$ROOT/web" && corepack yarn install --immutable && corepack yarn build)

ARTIFACTS_JSON=""
for arch in "${ARCHES[@]}"; do
    stage="$OUT/stage/$arch"
    echo "==> Publishing linux-$arch"

    for project in Panel Agent; do
        component="$(echo "$project" | tr '[:upper:]' '[:lower:]')"
        dotnet publish "$ROOT/src/TurboManager.$project" -c Release -r "linux-$arch" --self-contained \
            -p:PublishSingleFile=true -p:DebugType=none -p:Version="$VERSION" --nologo -v quiet \
            -o "$stage/$component"
    done

    cp -r "$ROOT/web/dist" "$stage/panel/wwwroot"
    mkdir -p "$stage/bin" "$stage/systemd"
    cp "$ROOT/install/bin/"* "$stage/bin/"
    cp "$ROOT/install/systemd/"*.service "$stage/systemd/"
    chmod 0755 "$stage/bin/"*
    echo "$VERSION" > "$stage/VERSION"
    # The key later updates are checked with: kept by --bundle installs, and handed over by this release (rotation).
    bundle_key > "$stage/release-key.pem"

    file="tm-$VERSION-linux-$arch.tar.gz"
    tar -czf "$OUT/files/$file" -C "$stage" .
    sha="$(sha256sum "$OUT/files/$file" | awk '{print $1}')"

    [[ -n "$ARTIFACTS_JSON" ]] && ARTIFACTS_JSON+=","
    ARTIFACTS_JSON+="\"$arch\":{\"url\":\"$DOWNLOAD_BASE/$file\",\"sha256\":\"$sha\"}"
done

for channel in "${CHANNELS[@]}"; do
    write_channel "$OUT/channels/$channel.json" "$VERSION" "$channel" "{$ARTIFACTS_JSON}" "$NOTES"
    sign_file "$OUT/channels/$channel.json"
done

cp "$ROOT/install/install.sh" "$OUT/install.sh"
embed_public_key "$OUT/install.sh"

if [[ $DOCS -eq 1 ]]; then
    echo "==> Building the documentation site"
    (cd "$ROOT/docs/site" && corepack yarn install --immutable && corepack yarn build)
    cp -r "$ROOT/docs/site/.vitepress/dist" "$OUT/docs"
fi
rm -rf "$OUT/stage"

echo "==> Done: $OUT"
ls -la "$OUT/files"
