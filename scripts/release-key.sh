#!/usr/bin/env bash
# Release signing helpers, sourced by scripts/package.sh and test-env/release.sh.
#
# Channel files are signed with an ECDSA P-256 key (SHA-256, DER signature, base64 in <file>.sig). install.sh checks
# them with openssl and the panel's agent with .NET; both trust only the public key embedded in install.sh.
#
# The private key comes from RELEASE_SIGNING_KEY (PEM text, e.g. a CI secret) or RELEASE_SIGNING_KEY_FILE. Without
# either, a development key is created in .release-dev-key.pem (gitignored): fine for the Docker test environment,
# never for a public release (servers installed from it only trust releases signed with that same local key).

# Sets SIGNING_KEY to a PEM file holding the private key.
resolve_signing_key() {
    local root="$1"

    if [[ -n "${RELEASE_SIGNING_KEY:-}" ]]; then
        SIGNING_KEY="$(mktemp)"
        printf '%s\n' "$RELEASE_SIGNING_KEY" > "$SIGNING_KEY"
    elif [[ -n "${RELEASE_SIGNING_KEY_FILE:-}" ]]; then
        SIGNING_KEY="$RELEASE_SIGNING_KEY_FILE"
    else
        SIGNING_KEY="$root/.release-dev-key.pem"
        if [[ ! -f "$SIGNING_KEY" ]]; then
            echo "==> No RELEASE_SIGNING_KEY: creating a development signing key in $SIGNING_KEY" >&2
            openssl ecparam -name prime256v1 -genkey -noout -out "$SIGNING_KEY" 2>/dev/null
        fi
        echo "==> Signing with the development key (not for public releases)" >&2
    fi

    openssl ec -in "$SIGNING_KEY" -noout 2>/dev/null || { echo "The release signing key is not an EC private key." >&2; exit 1; }
}

public_key() { openssl ec -in "$SIGNING_KEY" -pubout 2>/dev/null; }

# The key servers should trust after installing this release (release-key.pem in the bundle). Normally the signing
# key itself; to rotate, sign one release with the current key and set RELEASE_NEXT_PUBLIC_KEY_FILE to the new key's
# public half: servers that install it switch to the new key (see docs/site/guide/updates.md).
bundle_key() {
    if [[ -n "${RELEASE_NEXT_PUBLIC_KEY_FILE:-}" ]]; then
        openssl pkey -pubin -in "$RELEASE_NEXT_PUBLIC_KEY_FILE" 2>/dev/null || { echo "RELEASE_NEXT_PUBLIC_KEY_FILE is not a public key." >&2; exit 1; }
    else
        public_key
    fi
}

# sign_file <file>: writes <file>.sig
sign_file() {
    openssl dgst -sha256 -sign "$SIGNING_KEY" "$1" | base64 | tr -d '\r\n' > "$1.sig"
    printf '\n' >> "$1.sig"
}

# embed_public_key <install.sh>: puts the public key in place of the __RELEASE_PUBLIC_KEY__ line.
embed_public_key() {
    local key
    key="$(public_key)"
    KEY="$key" awk '$0 == "__RELEASE_PUBLIC_KEY__" { print ENVIRON["KEY"]; next } { print }' "$1" > "$1.tmp"
    mv "$1.tmp" "$1"
    grep -q "BEGIN PUBLIC KEY" "$1" || { echo "install.sh has no __RELEASE_PUBLIC_KEY__ line to replace." >&2; exit 1; }
}

# write_channel <file> <version> <channel> <artifacts-json> [notes]: the channel file (published = now, UTC).
write_channel() {
    VERSION="$2" CHANNEL="$3" ARTIFACTS="$4" NOTES="${5:-}" node -e '
        const e = process.env;
        process.stdout.write(JSON.stringify({
            version: e.VERSION, channel: e.CHANNEL, published: new Date().toISOString().replace(/\.\d+Z$/, "Z"),
            notes: e.NOTES, artifacts: JSON.parse(e.ARTIFACTS),
        }) + "\n");' > "$1"
}
