#!/usr/bin/env bash
# Checks run *inside* the server container after the API flow: did the panel's firewall
# changes really land in ufw, and did the unconfirmed one really get reverted?
set -euo pipefail

pass() { printf '  \033[32m✔\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$*" >&2; exit 1; }

echo "Server post-checks"

if ! ufw status | grep -q "Status: active"; then
    echo "  - firewall not active in this container; skipping ufw checks"
    exit 0
fi

ufw status | grep -qE "^30000/tcp +ALLOW" || fail "rule for 30000/tcp should be in ufw"
pass "allow rule present in ufw"

ufw status | grep -qE "^22/tcp +DENY +198\.51\.100\.66" || fail "confirmed deny rule should be in ufw"
pass "confirmed deny rule present in ufw"

if ufw status | grep -q "198.51.100.77"; then
    fail "unconfirmed rule should have been reverted from ufw"
fi
pass "unconfirmed rule reverted in ufw"

[[ ! -f /var/lib/turbo-manager-agent/pending-firewall-change.json ]] || fail "no pending change should remain on disk"
pass "no pending change left on disk"

echo "Server post-checks passed."
