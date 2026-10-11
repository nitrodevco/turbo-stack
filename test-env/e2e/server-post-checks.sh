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

nginx -t 2>/dev/null || fail "nginx config should still be valid"
[[ -L /etc/nginx/sites-enabled/tm-e2e-http.conf ]] || fail "site should be enabled via symlink"
[[ ! -f /etc/nginx/sites-available/tm-e2e-evil.conf ]] || fail "refused config must not be written"
[[ "$(stat -c '%a' /etc/turbo-manager/certs/secure.hotel.test/privkey.pem)" == "600" ]] || fail "private key must be 0600"
pass "nginx valid, sites enabled, refused config absent, key private"

grep -q "real_ip_header CF-Connecting-IP;" /etc/nginx/conf.d/tm-cloudflare-realip.conf || fail "Cloudflare real-IP config should be installed"
[[ -f /etc/turbo-manager/setup-closed && ! -e /etc/nginx/sites-enabled/tm-setup.conf ]] || fail "setup address should be closed"
if ufw status | grep -qE "^8443/tcp"; then
    fail "the setup port rule should be gone"
fi
pass "real-IP config installed; setup site and port closed"

echo "Server post-checks passed."
