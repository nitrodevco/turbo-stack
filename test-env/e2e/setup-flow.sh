#!/usr/bin/env bash
# End-to-end: claim a freshly installed panel through nginx on :8443, create the owner with
# TOTP 2FA, sign in again, and read system info through the root agent.
# Usage (inside the tester container): setup-flow.sh <setup-url>
set -euo pipefail

SETUP_URL="$1"
TOKEN="${SETUP_URL##*token=}"
BASE="https://server:8443"
JAR="$(mktemp)"
USERNAME="owner"
PASSWORD="correct horse battery staple"

pass() { printf '  \033[32m✔\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$*" >&2; exit 1; }

# -k: the setup address uses the installer's self-signed certificate.
call() {
    local method="$1" path="$2" body="${3:-}"
    curl -sk -b "$JAR" -c "$JAR" -X "$method" "$BASE$path" \
        -H 'X-TM-Request: 1' -H 'Content-Type: application/json' \
        ${body:+--data "$body"} -w '\n%{http_code}'
}

status_of() { tail -n1 <<<"$1"; }
body_of() { sed '$d' <<<"$1"; }

wait_next_totp_window() { sleep $(( 31 - $(date +%s) % 30 )); }

echo "Setup flow against $BASE"

response="$(call GET /api/setup/status)"
[[ "$(body_of "$response" | jq -r .claimed)" == "false" ]] || fail "panel should start unclaimed"
pass "panel is unclaimed"

response="$(call POST /api/setup/claim '{"token":"wrong"}')"
[[ "$(status_of "$response")" == "401" ]] || fail "wrong token should be rejected (got $(status_of "$response"))"
pass "wrong setup token rejected"

response="$(call POST /api/setup/claim "{\"token\":\"$TOKEN\"}")"
[[ "$(status_of "$response")" == "204" ]] || fail "claim failed: $response"
pass "setup token accepted"

response="$(call POST /api/setup/owner "{\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}")"
[[ "$(status_of "$response")" == "200" ]] || fail "owner creation failed: $response"
SECRET="$(body_of "$response" | jq -r .secret)"
pass "owner account created"

response="$(call POST /api/setup/owner/confirm "{\"code\":\"$(oathtool --totp -b "$SECRET")\"}")"
[[ "$(status_of "$response")" == "200" ]] || fail "2FA confirmation failed: $response"
[[ "$(body_of "$response" | jq '.recoveryCodes | length')" == "10" ]] || fail "expected 10 recovery codes"
pass "2FA enabled, 10 recovery codes issued"

response="$(call GET /api/setup/status)"
[[ "$(body_of "$response" | jq -r .claimed)" == "true" ]] || fail "panel should now be claimed"
response="$(call POST /api/setup/claim "{\"token\":\"$TOKEN\"}")"
[[ "$(status_of "$response")" == "409" ]] || fail "setup should be locked after completion"
pass "setup locked after completion"

response="$(call GET /api/system/info)"
[[ "$(status_of "$response")" == "200" ]] || fail "system info failed: $response"
[[ "$(body_of "$response" | jq -r .hostname)" == "hotel-test" ]] || fail "unexpected hostname: $response"
[[ "$(body_of "$response" | jq -r .container)" == "docker" ]] || fail "container not detected: $response"
pass "system info served by the root agent ($(body_of "$response" | jq -r .osName))"

call POST /api/auth/logout >/dev/null
response="$(call GET /api/auth/me)"
[[ "$(status_of "$response")" == "401" ]] || fail "logout did not end the session"
pass "signed out"

response="$(call POST /api/auth/login "{\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}")"
[[ "$(body_of "$response" | jq -r .mfaRequired)" == "true" ]] || fail "login should require 2FA: $response"
response="$(call GET /api/auth/me)"
[[ "$(status_of "$response")" == "401" ]] || fail "password alone must not grant a session"
pass "password accepted, second factor required"

# The code from setup can't be replayed; wait for the next 30-second window.
wait_next_totp_window
response="$(call POST /api/auth/mfa "{\"code\":\"$(oathtool --totp -b "$SECRET")\"}")"
[[ "$(status_of "$response")" == "200" ]] || fail "TOTP sign-in failed: $response"
pass "signed in with TOTP"

echo "Setup flow passed."
echo
echo "Server management against the real server"

response="$(call GET /api/metrics/current)"
[[ "$(status_of "$response")" == "200" ]] || fail "metrics failed: $response"
pass "live metrics from the agent"

response="$(call GET /api/services)"
[[ "$(body_of "$response" | jq -r '.[] | select(.name == "tm-panel.service") | .activeState')" == "active" ]] || fail "tm-panel.service should be listed as active"
pass "services listed (tm-panel.service active)"

response="$(call GET '/api/logs?unit=tm-panel.service&limit=5')"
[[ "$(status_of "$response")" == "200" ]] || fail "journal query failed: $response"
pass "journal query works"

response="$(call GET /api/ssh/settings)"
[[ "$(body_of "$response" | jq '.ports | index(22) != null')" == "true" ]] || fail "sshd settings should report port 22: $response"
pass "sshd settings read via sshd -T"

response="$(call GET /api/firewall/fail2ban)"
[[ "$(body_of "$response" | jq '[.jails[].name] | contains(["sshd", "turbo-manager-panel"])')" == "true" ]] || fail "fail2ban jails missing: $response"
pass "fail2ban jails active (sshd, panel)"

response="$(call GET /api/firewall)"
[[ "$(body_of "$response" | jq -r .inSync)" == "true" ]] || fail "installer rules should match the panel's: $response"
pass "installer firewall rules adopted by the panel"

response="$(call POST /api/firewall/rules '{"name":"Game TCP","action":"allow","protocol":"tcp","ports":"30000","sourceKind":"Any"}')"
[[ "$(body_of "$response" | jq -r '.pending')" == "null" && "$(body_of "$response" | jq -r .changed)" == "true" ]] || fail "allow rule should apply immediately: $response"
pass "allow rule applied without confirmation"

response="$(call POST /api/firewall/rules '{"name":"Block scanner","action":"deny","protocol":"tcp","ports":"22","sourceKind":"Address","sourceAddress":"198.51.100.66"}')"
PENDING="$(body_of "$response" | jq -r '.pending.id')"
[[ "$PENDING" != "null" ]] || fail "deny rule should need confirmation: $response"
response="$(call POST "/api/firewall/changes/$PENDING/confirm")"
[[ "$(body_of "$response" | jq -r .status)" == "confirmed" ]] || fail "confirmation failed: $response"
pass "risky rule applied and confirmed"

response="$(call POST /api/firewall/rules '{"name":"Unconfirmed","action":"deny","protocol":"tcp","ports":"25","sourceKind":"Address","sourceAddress":"198.51.100.77"}')"
[[ "$(body_of "$response" | jq -r '.pending.id')" != "null" ]] || fail "deny rule should need confirmation: $response"
echo "  … waiting 70s for the unconfirmed change to revert"
sleep 70
response="$(call GET /api/firewall)"
[[ "$(body_of "$response" | jq -r .pending)" == "null" ]] || fail "change should no longer be pending: $response"
[[ "$(body_of "$response" | jq '[.rules[].name] | index("Unconfirmed")')" == "null" ]] || fail "reverted rule should be gone from the panel: $response"
pass "unconfirmed change reverted in the panel"

response="$(call GET /api/firewall/exposure)"
[[ "$(body_of "$response" | jq -r '.[] | select(.port == 7800) | .exposure')" == "local" ]] || fail "panel port should be local only: $response"
pass "exposure check: panel port is local only"

echo "Server management passed."

