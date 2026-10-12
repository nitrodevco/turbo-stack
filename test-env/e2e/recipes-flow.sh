#!/usr/bin/env bash
# The Turbo and Nitro recipes against the real projects: a whole hotel (emulator, admin panel, public site,
# client, imager, assets host) set up and deployed on a fresh server, then each part checked through nginx.
# Slow — it builds turbo-cloud and nitro-next. Run with: test-env/dev recipes
#
# LOCAL_NITRO=1 (set by `LOCAL_NITRO=/path/to/nitro-next test-env/dev recipes`): only the Nitro recipes, from
# that checkout's commits served by the fake GitHub as a private repository.
# Usage (inside the tester container): recipes-flow.sh <setup-url>
set -euo pipefail

SETUP_URL="$1"
TOKEN="${SETUP_URL##*token=}"
BASE="https://server:8443"
JAR="$(mktemp)"
LOCAL_NITRO="${LOCAL_NITRO:-}"

pass() { printf '  \033[32m✔\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$*" >&2; exit 1; }

call() {
    local method="$1" path="$2" body="${3:-}"
    curl -sk -b "$JAR" -c "$JAR" -X "$method" "$BASE$path" \
        -H 'X-TM-Request: 1' -H 'Content-Type: application/json' \
        ${body:+--data "$body"} -w '\n%{http_code}'
}

status_of() { tail -n1 <<<"$1"; }
body_of() { sed '$d' <<<"$1"; }

wait_for_job() {
    local limit="${2:-60}"
    for _ in $(seq 1 "$limit"); do
        [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" != "running" ]] && break
        sleep 1
    done
    [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" == "succeeded" ]] \
        || fail "job $1 failed: $(body_of "$(call GET "/api/jobs/$1")" | jq -r .output | tail -40)"
}

# Creates an app from a recipe and prints its id; private repositories get their deploy keys added on the fake GitHub.
create() {
    local response
    response="$(call POST "/api/recipes/$1" "$2")"
    [[ "$(status_of "$response")" == "200" ]] || fail "$1 recipe failed: $response"
    body_of "$response" | jq -r '.deployKeys // [] | .[] | select(.publicKey) | "\(.repository) \(.publicKey)"' | while read -r repository key; do
        curl -sf http://fake-github:8081/cgi-bin/admin --data action=add-key --data "repo=$repository" --data-urlencode "key=$key" >/dev/null
    done
    body_of "$response" | jq -r .appId
}

deploy() {
    local response started
    started="$(date +%s)"
    response="$(call POST "/api/apps/$1/deploy")"
    [[ "$(status_of "$response")" == "202" ]] || fail "deploy of $2 did not start: $response"
    wait_for_job "$(body_of "$response" | jq -r .jobId)" 2400
    pass "$2 deployed ($(( $(date +%s) - started ))s)"
}

site() { curl -s --max-time 20 -H "Host: $1" "http://server$2"; }
code() { curl -s --max-time 20 -o /dev/null -w '%{http_code}' -H "Host: $1" "http://server$2"; }

echo "Turbo and Nitro recipes${LOCAL_NITRO:+ (Nitro only, from the local checkout)}"

call POST /api/setup/claim "{\"token\":\"$TOKEN\"}" >/dev/null
SECRET="$(body_of "$(call POST /api/setup/owner '{"username":"owner","password":"correct horse battery staple"}')" | jq -r .secret)"
[[ "$(status_of "$(call POST /api/setup/owner/confirm "{\"code\":\"$(oathtool --totp -b "$SECRET")\"}")")" == "200" ]] || fail "setup failed"
pass "panel claimed"

RUNTIMES=('{"name":"node","channel":"24"}')
[[ -n "$LOCAL_NITRO" ]] || RUNTIMES+=('{"name":"dotnet","channel":"10.0"}')
for runtime in "${RUNTIMES[@]}"; do
    response="$(call POST "/api/runtimes/$(jq -r .name <<<"$runtime")/install" "$runtime")"
    wait_for_job "$(body_of "$response" | jq -r .jobId)" 900
done

if [[ -z "$LOCAL_NITRO" ]]; then
    SQL="$(body_of "$(call GET /api/databases/engines)" | jq -r '.[] | select(.recommended) | .engine')"
    response="$(call POST "/api/databases/$SQL/install")"
    wait_for_job "$(body_of "$response" | jq -r .jobId)" 900
    pass ".NET 10, Node 24 and $SQL installed"

    TURBO="$(create turbo-emulator '{
        "name": "turbo", "systemUser": "hotel", "domains": ["ws.hotel.test"], "tlsMode": "None",
        "inputs": {
            "hotelName": "Recipe Hotel", "ownerName": "owner",
            "adminPanelUrl": "https://admin.hotel.test", "siteUrl": "http://hotel.test", "clientUrl": "http://play.hotel.test",
            "gamedataPublicUrl": "http://hotel.test", "avatarImageUrl": "http://imager.hotel.test/?figure={figure}&size=l"
        }}')"
    ADMIN="$(create turbo-admin '{"name":"admin","systemUser":"hotel","domains":["admin.hotel.test"],"tlsMode":"SelfSigned"}')"
    WEB="$(create turbo-web '{
        "name": "web", "systemUser": "hotel", "domains": ["hotel.test"], "tlsMode": "None",
        "inputs": { "clientUrl": "http://play.hotel.test", "imagerUrl": "http://imager.hotel.test" }}')"
else
    pass "Node 24 installed"
fi

NITRO_SOURCE='"repository": "nitrodevco/nitro-next"'
[[ -z "$LOCAL_NITRO" ]] || NITRO_SOURCE='"repository": "nitrodevco/nitro-next", "private": true'
CLIENT="$(create nitro-client "{
    \"name\": \"client\", \"systemUser\": \"hotel\", $NITRO_SOURCE, \"domains\": [\"play.hotel.test\"], \"tlsMode\": \"None\",
    \"inputs\": { \"socketUrl\": \"ws://ws.hotel.test\", \"gamedataUrl\": \"http://hotel.test\", \"imagerUrl\": \"http://imager.hotel.test\", \"siteUrl\": \"http://hotel.test\" }}")"
# The imager needs real game data to start: nitrodev's public example host (as in nitro-imager's .env.example).
IMAGER="$(create nitro-imager "{
    \"name\": \"imager\", \"systemUser\": \"hotel\", $NITRO_SOURCE, \"domains\": [\"imager.hotel.test\"], \"tlsMode\": \"None\",
    \"inputs\": { \"assetsUrl\": \"https://images.nitrodev.co\", \"gamedataUrl\": \"https://nitrodev.co\" }}")"
create static-assets '{"name":"assets","systemUser":"hotel","domains":["assets.hotel.test"],"tlsMode":"None"}' >/dev/null

if [[ -z "$LOCAL_NITRO" ]]; then
    [[ "$(body_of "$(call GET /api/repositories)" | jq 'length')" == "2" ]] || fail "five apps should share two checkouts (turbo-cloud, nitro-next)"
    pass "six recipes set up: two repositories, one database, five apps, six sites"

    # One at a time: the .NET and Vite builds each want a lot of memory.
    deploy "$TURBO" "Turbo emulator (dotnet publish + migrate)"
    deploy "$ADMIN" "turbo-admin"
    deploy "$WEB" "turbo-web"
else
    [[ "$(body_of "$(call GET /api/repositories)" | jq 'length')" == "1" ]] || fail "client and imager should share one checkout"
    pass "Nitro recipes set up from a private repository (deploy key added)"
fi

deploy "$CLIENT" "Nitro client"
deploy "$IMAGER" "Nitro imager"

if [[ -z "$LOCAL_NITRO" ]]; then
    UPGRADE="$(curl -s --max-time 5 -i -H 'Host: ws.hotel.test' -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
        -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' http://server/ || true)"
    [[ "$UPGRADE" == *"101"* ]] || fail "the emulator's WebSocket should upgrade through nginx: $(head -3 <<<"$UPGRADE")"
    pass "players can open a WebSocket to the emulator"

    [[ "$(code hotel.test /gamedata/hashes)" == "200" ]] || fail "Turbo's gamedata should be served on the public site"
    [[ "$(site hotel.test /)" == *"<div id=\"root\""* ]] || fail "turbo-web should be served"
    WEB_API="$(code hotel.test /api/config)"
    [[ "$WEB_API" =~ ^(200|401|404)$ ]] || fail "turbo-web's /api/ should reach Turbo (got $WEB_API)"
    pass "public site, its API and gamedata are served"

    SERVER_IP="$(getent hosts server | awk '{print $1}')"
    [[ "$(curl -sk --resolve admin.hotel.test:443:"$SERVER_IP" https://admin.hotel.test/)" == *"<div id=\"root\""* ]] || fail "turbo-admin should be served over HTTPS"
    ADMIN_API="$(curl -sk -o /dev/null -w '%{http_code}' --resolve admin.hotel.test:443:"$SERVER_IP" https://admin.hotel.test/api/me)"
    [[ "$ADMIN_API" =~ ^(200|401|403|404)$ ]] || fail "turbo-admin's /api/ should reach Turbo (got $ADMIN_API)"
    pass "admin panel served over HTTPS with its API"
fi

PAGE="$(site play.hotel.test /index.html)"
[[ "$PAGE" == *'"nitro.config.url": ["./config/nitro-config.json","http://hotel.test/gamedata/external_variables/0","./config/site-config.json"]'* ]] \
    || fail "the client should load the shipped config, Turbo's variables and the site config: $(grep -o '.nitro.config.url.*' <<<"$PAGE")"
[[ "$(site play.hotel.test /config/site-config.json | jq -r '."socket.url"')" == "ws://ws.hotel.test" ]] || fail "site config should carry the socket address"
pass "client built with this hotel's addresses"

[[ "$(site imager.hotel.test /health | jq -r .ok)" == "true" ]] || fail "the imager should be healthy: $(site imager.hotel.test /health)"
[[ "$(curl -s --max-time 60 -o /dev/null -w '%{content_type}' -H 'Host: imager.hotel.test' 'http://server/avatarimage?figure=hd-180-1.ch-210-66.lg-270-82&size=n')" == image/* ]] \
    || fail "the imager should render an avatar"
pass "imager renders avatars"

echo "Recipes passed."
