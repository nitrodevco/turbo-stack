#!/usr/bin/env bash
# Panel self-update, end to end: install release N, build up some state, update to N+1 through the panel and check
# that nothing changed; a tampered channel file is refused; a release whose panel doesn't start is rolled back by itself.
# Run with: test-env/dev upgrade (it publishes the test releases between the phases).
#
# Usage (inside a long-lived tester container; state is kept in /tmp/upgrade):
#   upgrade-flow.sh prepare <setup-url>
#   upgrade-flow.sh update <version>
#   upgrade-flow.sh tampered <version>
#   upgrade-flow.sh broken <version>
set -euo pipefail

STATE=/tmp/upgrade
BASE="https://server:8443"
JAR="$STATE/cookies"
mkdir -p "$STATE"

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

wait_for_job() {
    for _ in $(seq 1 "${2:-120}"); do
        [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" != "running" ]] && break
        sleep 1
    done
    [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" == "succeeded" ]] \
        || fail "job $1 failed: $(body_of "$(call GET "/api/jobs/$1")" | jq -r .output | tail -20)"
}

# A TOTP code is accepted once: re-authenticate with the next one.
sudo_mode() {
    sleep $(( 31 - $(date +%s) % 30 ))
    [[ "$(status_of "$(call POST /api/account/sudo "{\"code\":\"$(oathtool --totp -b "$(cat "$STATE/secret")")\"}")")" == "200" ]] \
        || fail "re-authenticating failed"
}

health_version() { curl -sk --max-time 3 "$BASE/api/health" 2>/dev/null | jq -r .version 2>/dev/null || true; }

web() { curl -sf --max-time 10 -H "Host: web.hotel.test" "http://server$1"; }

# What must survive an update untouched.
snapshot() {
    {
        body_of "$(call GET /api/sites)" | jq -c '[.[] | {name, domains}] | sort_by(.name)'
        body_of "$(call GET /api/apps)" | jq -c '[.[] | {name, systemUser}] | sort_by(.name)'
        body_of "$(call GET /api/repositories)" | jq -c '[.[] | {fullName, publicKey}]'
        web /
        web /api/anything
    }
}

assert_unchanged() {
    [[ "$(status_of "$(call GET /api/auth/me)")" == "200" ]] || fail "the session from before the update should still be valid"
    [[ "$(curl -sk -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $(cat "$STATE/token")" "$BASE/api/apps")" == "200" ]] \
        || fail "the API token from before the update should still work"
    diff <(snapshot) "$STATE/snapshot" >&2 || fail "sites, apps, repositories or what they serve changed"
    pass "same session and API token; sites, apps, deploy keys and the running apps unchanged"
}

prepare() {
    local token="${1##*token=}" secret response repo key web api
    echo "Self-update: release N installed, state built up"

    call POST /api/setup/claim "{\"token\":\"$token\"}" >/dev/null
    secret="$(body_of "$(call POST /api/setup/owner '{"username":"owner","password":"correct horse battery staple"}')" | jq -r .secret)"
    echo "$secret" > "$STATE/secret"
    [[ "$(status_of "$(call POST /api/setup/owner/confirm "{\"code\":\"$(oathtool --totp -b "$secret")\"}")")" == "200" ]] || fail "setup failed"

    wait_for_job "$(body_of "$(call POST /api/runtimes/node/install '{"channel":"24"}')" | jq -r .jobId)" 300
    response="$(call POST /api/repositories '{"name":"hotel-web","systemUser":"hotel","repository":"https://github.com/billsonnn/hotel-web","branch":"main","private":true,"submodules":true,"privateSubmodules":["billsonnn/hotel-theme"]}')"
    repo="$(body_of "$response" | jq -r .id)"
    for key in "billsonnn/hotel-web $(body_of "$response" | jq -r .publicKey)" "billsonnn/hotel-theme $(body_of "$response" | jq -r '.submoduleKeys[0].publicKey')"; do
        curl -sf http://fake-github:8081/cgi-bin/admin --data action=add-key --data "repo=${key%% *}" --data-urlencode "key=${key#* }" >/dev/null
    done

    web="$(body_of "$(call POST /api/apps "$(jq -n --argjson repo "$repo" '{name: "web", repositoryId: $repo, runtimes: ["node"], staticOutput: "dist",
        buildScript: "mkdir -p dist\ncp index.html dist/\ncp theme/theme.css dist/"}')")" | jq -r .id)"
    api="$(body_of "$(call POST /api/apps "$(jq -n --argjson repo "$repo" --arg build 'mkdir -p dist
cat > dist/server.js <<"JS"
require("http").createServer((q, s) => s.end(q.url === "/health" ? "ok" : "api says " + process.env.GREETING)).listen(9500, "127.0.0.1");
JS' '{name: "api", repositoryId: $repo, runtimes: ["node"], buildScript: $build, serviceCommand: "node dist/server.js", servicePort: 9500, healthPath: "/health"}')")" | jq -r .id)"
    call PUT "/api/apps/$api/environment" '{"GREETING":"hello"}' >/dev/null
    wait_for_job "$(body_of "$(call POST "/api/apps/$web/deploy")" | jq -r .jobId)" 300
    wait_for_job "$(body_of "$(call POST "/api/apps/$api/deploy")" | jq -r .jobId)" 300
    call POST /api/sites "$(jq -n --arg static "$(body_of "$(call GET "/api/apps/$web")" | jq -r .app.staticPath)" '{
        name: "hotel-web", domains: ["web.hotel.test"], tlsMode: "None",
        routes: [{path: "/", kind: "Spa", target: $static}, {path: "/api/", kind: "Proxy", target: "http://127.0.0.1:9500/"}]}')" >/dev/null
    [[ "$(web /)" == *"Hello from v1"* && "$(web /api/anything)" == "api says hello" ]] || fail "the deployed apps should be served"

    sudo_mode
    body_of "$(call POST /api/account/tokens '{"name":"ci","permissions":["apps.view"],"expiresInDays":1}')" | jq -r .token > "$STATE/token"
    snapshot > "$STATE/snapshot"
    pass "panel claimed; apps deployed and served; API token created"

    response="$(call POST /api/panel-update/check)"
    [[ "$(status_of "$response")" == "200" && "$(body_of "$response" | jq -r .error)" == "null" ]] || fail "checking the signed channel should work: $response"
    response="$(body_of "$(call GET /api/panel-update)")"
    [[ "$(jq -r .currentVersion <<<"$response")" == "0.1.0" && "$(jq -r .available <<<"$response")" == "false" ]] \
        || fail "0.1.0 should be installed and up to date: $response"
    pass "the signed channel verifies against the key from install time; up to date"
}

done_count() { body_of "$(call GET '/api/audit?action=panel.update.done')" | jq -r '.entries | length'; }

update() {
    local version="$1" response before
    before="$(done_count)"
    echo "Self-update to $version"

    response="$(call POST /api/panel-update/check)"
    [[ "$(body_of "$response" | jq -r .latestVersion)" == "$version" && "$(body_of "$response" | jq -r .available)" == "true" ]] \
        || fail "$version should be offered: $response"
    sudo_mode
    response="$(call POST /api/panel-update/apply "{\"version\":\"$version\"}")"
    [[ "$(status_of "$response")" == "202" ]] || fail "starting the update failed: $response"

    for _ in $(seq 1 150); do
        [[ "$(health_version)" == "$version" ]] && break
        sleep 2
    done
    [[ "$(health_version)" == "$version" ]] || fail "the panel should come back as $version"
    pass "updated through the panel: $version is running"

    for _ in $(seq 1 30); do
        response="$(body_of "$(call GET /api/panel-update)")"
        [[ "$(jq -r .last.state <<<"$response")" == "done" ]] && break
        sleep 2
    done
    [[ "$(jq -r .last.state <<<"$response")" == "done" && "$(jq -r .last.to <<<"$response")" == "$version" ]] \
        || fail "the update should be recorded as done: $response"
    [[ "$(done_count)" == "$(( before + 1 ))" ]] || fail "the update should be in the activity log once"
    pass "recorded in the activity log"
    assert_unchanged
}

tampered() {
    local version="$1" response current
    echo "A tampered channel file ($version)"
    current="$(health_version)"

    response="$(call POST /api/panel-update/check)"
    [[ "$(body_of "$response" | jq -r .error)" == *"signature doesn't match the release key"* ]] || fail "the tampered channel should be refused: $response"
    sudo_mode
    response="$(call POST /api/panel-update/apply "{\"version\":\"$version\"}")"
    [[ "$(status_of "$response")" == "422" ]] || fail "updating from a tampered channel must be refused: $response"
    sleep 5
    [[ "$(health_version)" == "$current" ]] || fail "nothing should have changed"
    pass "signature mismatch refused before anything was downloaded or changed"
}

broken() {
    local version="$1" response current
    echo "A release that doesn't start ($version)"
    current="$(health_version)"

    sudo_mode
    response="$(call POST /api/panel-update/apply "{\"version\":\"$version\"}")"
    [[ "$(status_of "$response")" == "202" ]] || fail "starting the update failed: $response"

    # ts-update waits up to two minutes for the new version, then puts the old one back.
    for _ in $(seq 1 200); do
        response="$(body_of "$(call GET /api/panel-update 2>/dev/null)" 2>/dev/null || true)"
        [[ "$(jq -r .last.state <<<"$response" 2>/dev/null)" == "rolled-back" && "$(jq -r .running <<<"$response")" == "false" ]] && break
        sleep 2
    done
    [[ "$(jq -r .last.state <<<"$response")" == "rolled-back" && "$(jq -r .last.to <<<"$response")" == "$version" ]] \
        || fail "the broken update should have been rolled back: $response"
    [[ "$(health_version)" == "$current" && "$(jq -r .currentVersion <<<"$response")" == "$current" ]] || fail "$current should be running again"
    [[ "$(jq -r .last.message <<<"$response")" == *"didn't start"* ]] || fail "the reason should be shown: $response"
    pass "$version didn't start; $current was put back automatically"

    (call POST /api/alerts/check >/dev/null)
    [[ "$(body_of "$(call GET /api/alerts)" | jq -r '.open[].title')" == *"update to Turbo Stack $version didn't work"* ]] \
        || fail "the failed update should raise an alert"
    pass "critical alert raised"
    assert_unchanged
}

case "${1:-}" in
    prepare) prepare "$2" ;;
    update) update "$2" ;;
    tampered) tampered "$2" ;;
    broken) broken "$2" ;;
    *) sed -n '2,11p' "$0"; exit 2 ;;
esac
echo "${1^} passed."
