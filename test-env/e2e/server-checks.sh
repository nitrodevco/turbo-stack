#!/usr/bin/env bash
# Checks run *inside* the server container after install.sh.
set -euo pipefail

pass() { printf '  \033[32m✔\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$*" >&2; exit 1; }

echo "Server checks"

for unit in ts-agent ts-panel nginx fail2ban; do
    systemctl is-active --quiet "$unit" || fail "$unit is not running"
done
pass "ts-agent, ts-panel, nginx and fail2ban are running"

[[ "$(stat -c '%U' /proc/"$(systemctl show -p MainPID --value ts-panel)")" == "turbo-stack" ]] || fail "panel must run as turbo-stack"
[[ "$(stat -c '%U' /proc/"$(systemctl show -p MainPID --value ts-agent)")" == "root" ]] || fail "agent must run as root"
pass "panel runs unprivileged, agent runs as root"

SOCKET=/run/turbo-stack/agent.sock
[[ "$(stat -c '%a %G' "$SOCKET")" == "660 turbo-stack" ]] || fail "agent socket must be 0660 group turbo-stack (got $(stat -c '%a %G' "$SOCKET"))"
pass "agent socket permissions are 0660 turbo-stack"

ping_agent_as() {
    sudo -u "$1" curl -s --max-time 5 --http2-prior-knowledge --unix-socket "$SOCKET" \
        -X POST -H 'content-type: application/grpc' -H 'te: trailers' --data-binary '' \
        http://agent/turbomanager.agent.v1.SystemAgent/Ping -o /dev/null
}

# Control: the panel's user can talk to the agent, so a rejection below really is the peer check.
ping_agent_as turbo-stack || fail "agent should accept the panel user"

# A user in the socket's group but not the panel user must still be rejected by the peer-credential check.
id intruder >/dev/null 2>&1 || useradd --no-create-home --groups turbo-stack intruder
if ping_agent_as intruder; then
    fail "agent accepted a connection from an unauthorized user"
fi
pass "agent accepts the panel user and rejects other users (peer credentials)"

if ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw status | grep -q "8443/tcp" || fail "setup port 8443 should be allowed"
    pass "firewall active with the setup port open"
else
    echo "  - firewall not active in this container (reported, not failed)"
fi

[[ ! -f /var/lib/turbo-stack/setup-token.sha256 ]] || [[ "$(stat -c '%U %a' /var/lib/turbo-stack/setup-token.sha256)" == "turbo-stack 600" ]] \
    || fail "setup token hash must be private to turbo-stack"
pass "setup token hash is private"

ldconfig -p | grep -q 'libicuuc\.so' || fail "ICU should be installed (.NET apps such as Turbo abort without it)"
pass "ICU installed for .NET apps"

echo "Server checks passed."
