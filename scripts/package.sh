#!/usr/bin/env bash
# Builds release bundles the way the install domain serves them:
#
#   dist/install.sh
#   dist/channels/<channel>.json
#   dist/files/tm-<version>-linux-<arch>.tar.gz
#
# Usage: scripts/package.sh [--arch x64|arm64]... [--channel stable|beta]
# Environment: VERSION overrides the version from Directory.Build.props.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist"
CHANNEL="stable"
ARCHES=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) ARCHES+=("$2"); shift 2 ;;
        --channel) CHANNEL="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

[[ ${#ARCHES[@]} -gt 0 ]] || ARCHES=(x64 arm64)
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")}"

echo "==> Packaging $VERSION ($CHANNEL) for ${ARCHES[*]}"
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
    echo "$VERSION" > "$stage/VERSION"

    file="tm-$VERSION-linux-$arch.tar.gz"
    tar -czf "$OUT/files/$file" -C "$stage" .
    sha="$(sha256sum "$OUT/files/$file" | awk '{print $1}')"

    [[ -n "$ARTIFACTS_JSON" ]] && ARTIFACTS_JSON+=","
    ARTIFACTS_JSON+="\"$arch\":{\"url\":\"files/$file\",\"sha256\":\"$sha\"}"
done

cat > "$OUT/channels/$CHANNEL.json" <<EOF
{"version":"$VERSION","channel":"$CHANNEL","artifacts":{$ARTIFACTS_JSON}}
EOF

cp "$ROOT/install/install.sh" "$OUT/install.sh"
rm -rf "$OUT/stage"

echo "==> Done: $OUT"
ls -la "$OUT/files"
