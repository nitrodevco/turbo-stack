#!/usr/bin/env bash
# "Minutes to a hotel": the wizard's API sets up a whole Turbo + Nitro hotel from the local turbo-cloud and
# nitro-next commits (served by the fake GitHub as private repositories), then a domain change is previewed
# and applied. Run with: test-env/dev hotel
# Usage (inside the tester container): hotel-flow.sh <setup-url>
set -euo pipefail

SETUP_URL="$1"
TOKEN="${SETUP_URL##*token=}"
BASE="https://server:8443"
JAR="$(mktemp)"
SERVER_IP="$(getent hosts server | awk '{print $1}')"

pass() { printf '  \033[32m✔\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$*" >&2; exit 1; }

call() {
    local method="$1" path="$2" body="${3:-}"
    curl -sk -b "$JAR" -c "$JAR" -X "$method" "$BASE$path" \
        -H 'X-TS-Request: 1' -H 'Content-Type: application/json' \
        ${body:+--data "$body"} -w '\n%{http_code}'
}

status_of() { tail -n1 <<<"$1"; }
body_of() { sed '$d' <<<"$1"; }

# Waits for a job and prints its final status.
job_status() {
    for _ in $(seq 1 "${2:-3600}"); do
        [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" != "running" ]] && break
        sleep 1
    done
    body_of "$(call GET "/api/jobs/$1")" | jq -r .status
}

job_output() { body_of "$(call GET "/api/jobs/$1")" | jq -r .output; }

# HTTPS through nginx with the self-signed certificates, resolving the hotel's names to the server.
https() { curl -sk --max-time 30 --resolve "$1:443:$SERVER_IP" "https://$1$2"; }

echo "A hotel in minutes (wizard API, local turbo-cloud + nitro-next)"

call POST /api/setup/claim "{\"token\":\"$TOKEN\"}" >/dev/null
SECRET="$(body_of "$(call POST /api/setup/owner '{"username":"owner","password":"correct horse battery staple"}')" | jq -r .secret)"
[[ "$(status_of "$(call POST /api/setup/owner/confirm "{\"code\":\"$(oathtool --totp -b "$SECRET")\"}")")" == "200" ]] || fail "setup failed"
pass "panel claimed"

# Assets from nitrodev's public example host; the imager reads figure data from there too (this test hotel's
# own gamedata isn't reachable by name from inside the server).
response="$(call POST /api/stacks '{
    "name": "hotel", "preset": "turbo-nitro", "domain": "hotel.test", "tlsMode": "SelfSigned",
    "hosts": { "assets": "" },
    "settings": {
        "hotelName": "E2E Hotel", "ownerName": "owner",
        "assets.url": "https://images.nitrodev.co", "assets.badgeUrl": "https://images.nitrodev.co/images/badges/%badgename%.gif"
    },
    "overrides": { "imager.gamedataUrl": "https://nitrodev.co" },
    "sources": [
        { "default": "nitrodevco/turbo-cloud", "repository": "nitrodevco/turbo-cloud", "branch": "main", "private": true },
        { "default": "nitrodevco/nitro-next", "repository": "nitrodevco/nitro-next", "branch": "main", "private": true }
    ]}')"
[[ "$(status_of "$response")" == "200" ]] || fail "creating the hotel failed: $response"
STACK="$(body_of "$response" | jq -r .id)"
pass "hotel created from the preset"

started="$(date +%s)"
JOB="$(body_of "$(call POST "/api/stacks/$STACK/install")" | jq -r .jobId)"
[[ "$(job_status "$JOB")" == "failed" ]] || fail "the first install should stop for the deploy keys"
[[ "$(job_output "$JOB")" == *"add its deploy key on GitHub"* ]] || fail "the install should ask for the deploy keys: $(job_output "$JOB" | tail -20)"
pass "install set up runtimes, database, apps, sites and certificates, then asked for the deploy keys"

body_of "$(call GET /api/repositories)" | jq -r '.[] | "\(.fullName) \(.publicKey)"' | while read -r repository key; do
    curl -sf http://fake-github:8081/cgi-bin/admin --data action=add-key --data "repo=$repository" --data-urlencode "key=$key" >/dev/null
done

JOB="$(body_of "$(call POST "/api/stacks/$STACK/install")" | jq -r .jobId)"
[[ "$(job_status "$JOB")" == "succeeded" ]] || fail "the install should finish once the keys are added: $(job_output "$JOB" | tail -60)"
pass "hotel installed and deployed ($(( $(date +%s) - started ))s in total)"

MAP="$(body_of "$(call GET "/api/stacks/$STACK")")"
[[ "$(jq '[.roles[] | select(.included and .app != null and .app.currentRelease == null)] | length' <<<"$MAP")" == "0" ]] \
    || fail "every app should be live: $(jq -c '[.roles[] | {role, app: .app.currentRelease}]' <<<"$MAP")"

UPGRADE="$(curl -sk --http1.1 --max-time 5 -i --resolve "ws.hotel.test:443:$SERVER_IP" -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' https://ws.hotel.test/ || true)"
[[ "$UPGRADE" == *" 101 "* ]] || fail "wss://ws.hotel.test should reach the emulator: $(head -3 <<<"$UPGRADE")"
[[ "$(https hotel.test /)" == *'<div id="root"'* ]] || fail "the website should be served over HTTPS"
[[ "$(https admin.hotel.test /)" == *'<div id="root"'* ]] || fail "the admin panel should be served over HTTPS"
[[ "$(curl -sk -o /dev/null -w '%{http_code}' --resolve "hotel.test:443:$SERVER_IP" https://hotel.test/gamedata/hashes)" == "200" ]] \
    || fail "the emulator's gamedata should be served on the website"
pass "website, admin panel, gamedata and the emulator's WebSocket answer over HTTPS"

[[ "$(https play.hotel.test /config/site-config.json | jq -r '."socket.url"')" == "wss://ws.hotel.test" ]] || fail "the client should know the socket address"
[[ "$(https play.hotel.test /config/site-config.json | jq -r '."group.badge.url"')" == "https://imager.hotel.test/badge/%imagerdata%.gif" ]] \
    || fail "the client should know the imager's address"
[[ "$(https play.hotel.test /index.html)" == *'https://hotel.test/gamedata/external_variables/0'* ]] || fail "the client should load the website's gamedata variables"
[[ "$(https hotel.test /)" == *'<div id="root"'* && "$(curl -sk -D - -o /dev/null --resolve "hotel.test:443:$SERVER_IP" https://hotel.test/)" == *"frame-src https://play.hotel.test"* ]] \
    || fail "the website should allow the client as a frame"
[[ "$(curl -sk -o /dev/null -w '%{content_type}' --resolve "imager.hotel.test:443:$SERVER_IP" 'https://imager.hotel.test/avatarimage?figure=hd-180-1.ch-210-66&size=n')" == image/* ]] \
    || fail "the imager should render avatars"
pass "everything wired: client → socket, imager, gamedata; website → client frame; imager renders"

# A domain change: preview first, then apply.
response="$(call PUT "/api/stacks/$STACK" '{"tlsMode":"SelfSigned","hosts":{"client":"game.hotel.test","assets":""}}')"
[[ "$(status_of "$response")" == "200" ]] || fail "changing the client's domain failed: $response"
CHANGES="$(body_of "$response")"
action() { jq -r --arg role "$1" '.[] | select(.role == $role) | .action' <<<"$CHANGES"; }
[[ "$(action client)-$(action emulator)-$(action web)-$(action imager)" == "reload-restart-reload-none" ]] \
    || fail "preview should be: client reload, emulator restart, website reload, imager untouched: $CHANGES"
pass "preview: client reload, emulator restart, website reload, nothing else"

JOB="$(body_of "$(call POST "/api/stacks/$STACK/apply")" | jq -r .jobId)"
[[ "$(job_status "$JOB" 600)" == "succeeded" ]] || fail "applying the change failed: $(job_output "$JOB" | tail -30)"
[[ "$(https game.hotel.test /config/site-config.json | jq -r '."socket.url"')" == "wss://ws.hotel.test" ]] || fail "the client should be served on its new domain"
[[ "$(curl -sk -D - -o /dev/null --resolve "hotel.test:443:$SERVER_IP" https://hotel.test/)" == *"frame-src https://game.hotel.test"* ]] \
    || fail "the website should now allow the client's new domain"
EMULATOR="$(jq -r '.roles[] | select(.role == "emulator") | .app.id' <<<"$MAP")"
[[ "$(body_of "$(call GET "/api/apps/$EMULATOR/environment")" | jq -r .TURBO__Turbo__Web__ClientUrl)" == "https://game.hotel.test/?sso={ticket}" ]] \
    || fail "the emulator should send players to the new domain"
UPGRADE="$(curl -sk --http1.1 --max-time 5 -i --resolve "ws.hotel.test:443:$SERVER_IP" -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' https://ws.hotel.test/ || true)"
[[ "$UPGRADE" == *" 101 "* ]] || fail "the emulator should be back after its restart"
pass "domain change applied: client on game.hotel.test with a new certificate, website and emulator updated"

# Optional parts: the admin panel goes (the emulator is pointed elsewhere), then comes back.
response="$(curl -sk -b "$JAR" -c "$JAR" -X DELETE "$BASE/api/stacks/$STACK/roles/admin" -H 'X-TS-Request: 1' -H 'Content-Type: application/json'     --data '{"replacements":{"admin.url":"https://staff.example.com"}}' -w '
%{http_code}')"
[[ "$(status_of "$response")" == "202" ]] || fail "removing the admin panel failed: $response"
JOB="$(body_of "$response" | jq -r .jobId)"
[[ "$(job_status "$JOB" 600)" == "succeeded" ]] || fail "removing the admin panel failed: $(job_output "$JOB" | tail -30)"
[[ "$(body_of "$(call GET "/api/apps/$EMULATOR/environment")" | jq -r .TURBO__Turbo__Admin__PanelUrl)" == "https://staff.example.com" ]]     || fail "the emulator should use the admin address given instead"
[[ "$(body_of "$(call GET /api/sites)" | jq '[.[] | select(.domains | index("admin.hotel.test"))] | length')" == "0" ]]     || fail "the admin panel's site should be gone"
pass "admin panel removed: its app and site are gone, the emulator points at the address given instead"

JOB="$(body_of "$(call POST "/api/stacks/$STACK/roles/admin" '{}')" | jq -r .jobId)"
[[ "$(job_status "$JOB" 1200)" == "succeeded" ]] || fail "adding the admin panel back failed: $(job_output "$JOB" | tail -40)"
[[ "$(https admin.hotel.test /)" == *'<div id="root"'* ]] || fail "the admin panel should be served again"
[[ "$(body_of "$(call GET "/api/apps/$EMULATOR/environment")" | jq -r .TURBO__Turbo__Admin__PanelUrl)" == "https://admin.hotel.test" ]]     || fail "the emulator should be wired to the admin panel again"
pass "admin panel added back: created, certified, deployed and wired"

[[ "$(status_of "$(call GET "/api/stacks/$STACK/answers")")" == "200" ]] || fail "the answers file should download"
pass "answers file downloads (for rebuilding this hotel)"

echo "Hotel passed."
