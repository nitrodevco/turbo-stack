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
echo
echo "Sites"

response="$(call POST /api/sites '{"name":"e2e-http","domains":["site.hotel.test"],"tlsMode":"None","routes":[{"path":"/","kind":"Static","target":"/var/www/e2e-http"},{"path":"/health/","kind":"Proxy","target":"http://127.0.0.1:7800/api/health"}]}')"
[[ "$(body_of "$response" | jq -r .applied)" == "true" ]] || fail "HTTP site should apply: $response"
curl -sf -H "Host: site.hotel.test" http://server/ | grep -q "It works" || fail "nginx should serve the site's placeholder page"
curl -sf -H "Host: site.hotel.test" http://server/health/ | grep -q '"ok"' || fail "proxy route should reach the panel health endpoint"
pass "HTTP site served by nginx (static + proxy routes)"

response="$(call POST /api/sites '{"name":"e2e-tls","domains":["secure.hotel.test"],"tlsMode":"SelfSigned","routes":[{"path":"/","kind":"Spa","target":"/var/www/e2e-tls"}]}')"
JOB="$(body_of "$response" | jq -r .certificateJobId)"
[[ "$JOB" != "null" ]] || fail "self-signed site should start a certificate job: $response"
for _ in $(seq 1 30); do
    [[ "$(body_of "$(call GET "/api/jobs/$JOB")" | jq -r .status)" != "running" ]] && break
    sleep 1
done
[[ "$(body_of "$(call GET "/api/jobs/$JOB")" | jq -r .status)" == "succeeded" ]] || fail "certificate job failed: $(call GET "/api/jobs/$JOB")"
curl -sfk --resolve secure.hotel.test:443:"$(getent hosts server | awk '{print $1}')" https://secure.hotel.test/ | grep -q "It works" \
    || fail "site should be served over HTTPS after the certificate job"
[[ "$(curl -s -o /dev/null -w '%{http_code}' -H "Host: secure.hotel.test" http://server/)" == "301" ]] || fail "HTTP should redirect to HTTPS"
pass "self-signed HTTPS site: certificate issued, HTTPS served, HTTP redirected"

response="$(call POST /api/sites '{"name":"e2e-evil","domains":["evil.hotel.test"],"tlsMode":"None","customConfig":"server { listen 80; server_name evil.hotel.test; location / { alias /etc/; } }"}')"
[[ "$(body_of "$response" | jq -r .applied)" == "false" ]] || fail "unsafe custom config must be refused: $response"
pass "unsafe custom config refused by the agent's policy"

echo "Sites passed."
echo
echo "Databases (real MySQL and PostgreSQL)"

wait_for_job() {
    local limit="${2:-60}"
    for _ in $(seq 1 "$limit"); do
        [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" != "running" ]] && break
        sleep 1
    done
    [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" == "succeeded" ]] \
        || fail "job $1 failed: $(body_of "$(call GET "/api/jobs/$1")" | jq -r .output | tail -20)"
}

# Turbo gets whichever of MySQL/MariaDB the panel recommends for this Ubuntu release.
SQL="$(body_of "$(call GET /api/databases/engines)" | jq -r '.[] | select(.recommended) | .engine')"
[[ "$SQL" == mysql || "$SQL" == mariadb ]] || fail "MySQL or MariaDB should be recommended (got '$SQL')"
OTHER="$([[ "$SQL" == mysql ]] && echo mariadb || echo mysql)"

for engine in "$SQL" postgres; do
    response="$(call POST "/api/databases/$engine/install")"
    wait_for_job "$(body_of "$response" | jq -r .jobId)" 600
done
[[ "$(body_of "$(call GET /api/databases/engines)" | jq -r '[.[] | select(.installed and .running) | .engine] | join(",")')" == "$SQL,postgres" ]] \
    || fail "$SQL and PostgreSQL should be installed and running"
[[ -n "$(body_of "$(call GET /api/databases/engines)" | jq -r --arg other "$OTHER" '.[] | select(.engine == $other) | .blockedReason')" ]] \
    || fail "$OTHER should be blocked next to $SQL"
pass "$SQL (recommended) and PostgreSQL installed; $OTHER blocked next to it"

response="$(call POST "/api/databases/$SQL/databases" '{"name":"turbo","user":"turbo"}')"
[[ "$(body_of "$response" | jq -r '.credentials.password | length')" == "32" ]] || fail "database with user should return credentials: $response"
call PUT "/api/databases/$SQL/users/turbo/password" '{"password":"E2e-Mysql-Passw0rd"}' >/dev/null
response="$(curl -sk -b "$JAR" -c "$JAR" -H 'X-TM-Request: 1' -H 'Content-Type: application/octet-stream' \
    --data-binary $'CREATE TABLE players (id int);\nINSERT INTO players VALUES (1),(2);\n' "$BASE/api/databases/backups/upload?name=seed.sql")"
IMPORT="$(jq -r .id <<<"$response")"
[[ "$IMPORT" == imports/*seed.sql ]] || fail "upload failed: $response"
response="$(call POST "/api/databases/$SQL/restore" "{\"backupId\":\"$IMPORT\",\"database\":\"turbo\"}")"
wait_for_job "$(body_of "$response" | jq -r .jobId)" 120
response="$(call POST "/api/databases/$SQL/databases/turbo/backup")"
wait_for_job "$(body_of "$response" | jq -r .jobId)" 120
BACKUP="$(body_of "$(call GET "/api/databases/backups?engine=$SQL")" | jq -r '[.[] | select(.database == "turbo")][0].id')"
[[ "$(curl -sk -b "$JAR" "$BASE/api/databases/backups/download?id=$BACKUP" | gunzip | grep -c 'INSERT INTO .players.')" -ge 1 ]] \
    || fail "downloaded backup should contain the data"
pass "$SQL database + user created, import restored, backup taken and downloaded"

response="$(call POST /api/databases/postgres/databases '{"name":"studio","user":"studio"}')"
[[ "$(body_of "$response" | jq -r .credentials.port)" == "5432" ]] || fail "PostgreSQL database with user failed: $response"
call PUT /api/databases/postgres/users/studio/password '{"password":"E2e-Postgres-Passw0rd"}' >/dev/null
response="$(call POST /api/databases/postgres/databases/studio/backup)"
wait_for_job "$(body_of "$response" | jq -r .jobId)" 120
call POST /api/databases/postgres/databases '{"name":"other_app"}' >/dev/null
pass "PostgreSQL database + user created and backed up"

call POST "/api/databases/$SQL/databases" '{"name":"scratch"}' >/dev/null
[[ "$(body_of "$(call DELETE "/api/databases/$SQL/databases/scratch")" | jq -r .finalBackup)" == "$SQL"/scratch/* ]] \
    || fail "drop should keep a final backup"
pass "dropping a database keeps a final backup"

[[ "$(body_of "$(call GET /api/firewall/exposure)" | jq -r '[.[] | select(.port == 3306 or .port == 5432) | .exposure] | unique | join(",")')" == "local" ]] \
    || fail "database ports should only listen locally"
pass "exposure check: database ports are local only"

echo "Databases passed."
echo
echo "Deploys (fake GitHub)"

GITHUB_ADMIN="http://fake-github:8081/cgi-bin/admin"

# Waits for a job and prints its final status (for jobs that are expected to fail).
job_status() {
    for _ in $(seq 1 "${2:-120}"); do
        [[ "$(body_of "$(call GET "/api/jobs/$1")" | jq -r .status)" != "running" ]] && break
        sleep 1
    done
    body_of "$(call GET "/api/jobs/$1")" | jq -r .status
}

deploy_app() {
    local response
    response="$(call POST "/api/apps/$1/deploy")"
    [[ "$(status_of "$response")" == "202" ]] || fail "deploy of app $1 did not start: $response"
    body_of "$response" | jq -r .jobId
}

response="$(call POST /api/runtimes/node/install '{"channel":"24"}')"
wait_for_job "$(body_of "$response" | jq -r .jobId)" 300
[[ "$(body_of "$(call GET /api/runtimes)" | jq -r '[.[] | select(.name == "node" and .installed)] | length')" -ge 1 ]] || fail "node should be listed as installed"
pass "Node.js 24 runtime installed (checksum verified)"

response="$(call POST /api/repositories '{"name":"hotel-web","systemUser":"hotel","repository":"https://github.com/billsonnn/hotel-web","branch":"main","private":true,"submodules":true,"privateSubmodules":["billsonnn/hotel-theme"]}')"
[[ "$(status_of "$response")" == "200" ]] || fail "adding the repository failed: $response"
REPO="$(body_of "$response" | jq -r .id)"
WEB_KEY="$(body_of "$response" | jq -r .publicKey)"
THEME_KEY="$(body_of "$response" | jq -r '.submoduleKeys[0].publicKey')"
[[ "$WEB_KEY" == ssh-ed25519* && "$THEME_KEY" == ssh-ed25519* && "$WEB_KEY" != "$THEME_KEY" ]] || fail "expected one deploy key per repository: $response"

[[ "$(body_of "$(call POST "/api/repositories/$REPO/test")" | jq -r .ok)" == "false" ]] || fail "access should fail before the deploy key is added"
curl -sf "$GITHUB_ADMIN" --data action=add-key --data repo=billsonnn/hotel-web --data-urlencode "key=$WEB_KEY" >/dev/null
curl -sf "$GITHUB_ADMIN" --data action=add-key --data repo=billsonnn/hotel-theme --data-urlencode "key=$THEME_KEY" >/dev/null
response="$(call POST "/api/repositories/$REPO/test")"
[[ "$(body_of "$response" | jq -r .ok)" == "true" ]] || fail "access should work once the deploy key is added: $response"
pass "private repository: per-repository deploy keys generated, access verified"

response="$(call POST /api/apps "$(jq -n --argjson repo "$REPO" '{
    name: "web", repositoryId: $repo, runtimes: ["node"], staticOutput: "dist",
    sharedPaths: [{path: "uploads", directory: true}],
    buildScript: "mkdir -p dist\ncp index.html dist/\ncp theme/theme.css dist/\nnode --version > dist/node.txt"}')")"
[[ "$(status_of "$response")" == "200" ]] || fail "creating the web app failed: $response"
WEB="$(body_of "$response" | jq -r .id)"

API_BUILD='mkdir -p dist
cat > dist/server.js <<"JS"
require("http").createServer((q, s) => s.end(q.url === "/health" ? "ok" : "api says " + process.env.GREETING)).listen(9500, "127.0.0.1");
JS'
api_request() {
    jq -n --argjson repo "$REPO" --arg build "$1" '{
        name: "api", repositoryId: $repo, runtimes: ["node"], buildScript: $build,
        serviceCommand: "node dist/server.js", servicePort: 9500, healthPath: "/health"}'
}
response="$(call POST /api/apps "$(api_request "$API_BUILD")")"
[[ "$(status_of "$response")" == "200" ]] || fail "creating the api app failed: $response"
API="$(body_of "$response" | jq -r .id)"
call PUT "/api/apps/$API/environment" '{"GREETING":"hello"}' >/dev/null

wait_for_job "$(deploy_app "$WEB")" 300
wait_for_job "$(deploy_app "$API")" 300
STATIC="$(body_of "$(call GET "/api/apps/$WEB")" | jq -r .app.staticPath)"
pass "web (static) and api (service) deployed"

response="$(call POST /api/sites "$(jq -n --arg static "$STATIC" '{
    name: "hotel-web", domains: ["web.hotel.test"], tlsMode: "None",
    routes: [{path: "/", kind: "Spa", target: $static}, {path: "/api/", kind: "Proxy", target: "http://127.0.0.1:9500/"}]}')")"
[[ "$(body_of "$response" | jq -r .applied)" == "true" ]] || fail "site for the app should apply: $response"
web() { curl -sf -H "Host: web.hotel.test" "http://server$1"; }
[[ "$(web /)" == *"Hello from v1"* ]] || fail "site should serve the deployed page: $(web / || true)"
[[ "$(web /theme.css)" == *"rebeccapurple"* ]] || fail "private submodule content should be deployed: $(web /theme.css || true)"
[[ "$(web /node.txt)" == "v24"* ]] || fail "the build should run with the Node runtime: $(web /node.txt || true)"
[[ "$(web /api/anything)" == "api says hello" ]] || fail "service should run with its environment behind the proxy: $(web /api/anything || true)"
pass "site serves the release (private submodule, Node build, service + environment)"

# Auto-deploy: a push to GitHub sends a signed webhook.
call PUT "/api/apps/$WEB" "$(body_of "$(call GET "/api/apps/$WEB")" | jq '.app | .autoDeploy = true')" >/dev/null
curl -sf "$GITHUB_ADMIN" --data action=commit --data repo=billsonnn/hotel-web --data file=index.html \
    --data-urlencode 'content=<h1>Hello from v2</h1>' >/dev/null
SECRET_HOOK="$(body_of "$(call GET "/api/repositories/$REPO/webhook")" | jq -r .secret)"
PAYLOAD='{"ref":"refs/heads/main","commits":[{"added":[],"removed":[],"modified":["index.html"]}]}'
sign() { printf '%s' "$PAYLOAD" | openssl dgst -sha256 -hmac "$1" | sed 's/^.*= //'; }
hook() {
    curl -sk "$BASE/hooks/github/$REPO" -H 'Content-Type: application/json' -H 'X-GitHub-Event: push' \
        -H "X-GitHub-Delivery: $1" -H "X-Hub-Signature-256: sha256=$2" --data "$PAYLOAD" -w '\n%{http_code}'
}
[[ "$(status_of "$(hook e2e-forged "$(sign wrong-secret)")")" == "401" ]] || fail "a forged webhook must be rejected"
response="$(hook e2e-delivery-1 "$(sign "$SECRET_HOOK")")"
[[ "$(status_of "$response")" == "202" ]] || fail "signed webhook should be accepted: $response"
JOB="$(body_of "$response" | jq -r .result | sed -n 's/.*web: deploying (job \([^)]*\)).*/\1/p')"
[[ -n "$JOB" ]] || fail "webhook should start a deploy of web (and not api, which has auto-deploy off): $response"
[[ "$(status_of "$(hook e2e-delivery-1 "$(sign "$SECRET_HOOK")")")" == "409" ]] || fail "a replayed delivery must be rejected"
wait_for_job "$JOB" 300
[[ "$(web /)" == *"Hello from v2"* ]] || fail "auto-deploy should serve v2: $(web / || true)"
pass "signed push webhook auto-deployed v2 (forged and replayed deliveries rejected)"

V1="$(body_of "$(call GET "/api/apps/$WEB")" | jq -r '.releases[1].name')"
response="$(call POST "/api/apps/$WEB/releases/$V1/activate")"
wait_for_job "$(body_of "$response" | jq -r .jobId)"
[[ "$(web /)" == *"Hello from v1"* ]] || fail "rollback should serve v1 again: $(web / || true)"
pass "rolled back to the previous release instantly"

# A release whose service never becomes healthy is rolled back automatically.
GOOD_API="$(body_of "$(call GET "/api/apps/$API")" | jq -r .app.currentRelease)"
call PUT "/api/apps/$API" "$(api_request 'mkdir -p dist && echo "process.exit(1)" > dist/server.js')" >/dev/null
sleep 1 # release names are per second
[[ "$(job_status "$(deploy_app "$API")" 300)" == "failed" ]] || fail "deploying a broken service should fail"
[[ "$(body_of "$(call GET "/api/apps/$API")" | jq -r .app.currentRelease)" == "$GOOD_API" ]] || fail "the previous release should be live again"
[[ "$(web /api/health)" == "ok" ]] || fail "the service should be healthy on the previous release"
pass "unhealthy release rolled back automatically, service still up"

call PUT "/api/apps/$API" "$(api_request 'exit 3')" >/dev/null
[[ "$(job_status "$(deploy_app "$API")" 300)" == "failed" ]] || fail "a failing build should fail the deploy"
[[ "$(web /api/health)" == "ok" ]] || fail "a failed build must not touch the running service"
pass "failed build left the live release untouched"

echo "Deploys passed."
echo
echo "Cloudflare (fake API)"

SERVER_IP="$(getent hosts server | awk '{print $1}')"
FAKE="http://fake-cloudflare:8080"

call PUT /api/settings/server '{"publicIPv4":"203.0.113.10"}' >/dev/null
response="$(call PUT /api/cloudflare/token '{"token":"test-token"}')"
[[ "$(body_of "$response" | jq '[.checks[] | select(.status != "ok")] | length')" == "0" ]] || fail "all permission checks should pass: $response"
[[ "$(curl -s "$FAKE/__fake/state" | jq '.records | length')" == "0" ]] || fail "the DNS permission probe should clean up after itself"
pass "token connected, permissions probed, probe record removed"

response="$(call POST /api/sites '{"name":"e2e-cf","domains":["cf.hotel.test"],"tlsMode":"CloudflareOrigin","cloudflareDns":true,"routes":[{"path":"/","kind":"Spa","target":"/var/www/e2e-cf"}]}')"
[[ "$(body_of "$response" | jq -r .applied)" == "true" ]] || fail "Cloudflare site should apply: $response"
wait_for_job "$(body_of "$response" | jq -r .certificateJobId)"
[[ "$(curl -s "$FAKE/__fake/state" | jq -r '.records[] | select(.name == "cf.hotel.test") | "\(.type) \(.content) \(.proxied)"')" == "A 203.0.113.10 true" ]] \
    || fail "proxied A record should exist: $(curl -s "$FAKE/__fake/state")"
curl -sfk --resolve cf.hotel.test:443:"$SERVER_IP" https://cf.hotel.test/ | grep -q "It works" || fail "site should serve HTTPS with the Origin certificate"
pass "DNS record created, Origin certificate issued, HTTPS served"

response="$(call POST /api/cloudflare/zones/zone-hotel/preset)"
[[ "$(curl -s "$FAKE/__fake/state" | jq -r .settings.ssl)" == "strict" ]] || fail "preset should set SSL to strict: $response"
pass "recommended zone settings applied"

response="$(call PUT /api/panel-address '{"domain":"panel.hotel.test"}')"
[[ "$(body_of "$response" | jq -r .applied)" == "true" ]] || fail "panel site should apply: $response"
JOB="$(body_of "$response" | jq -r .certificateJobId)"
[[ "$JOB" == "null" ]] || wait_for_job "$JOB"
CODE="$(body_of "$(call GET /api/panel-address)" | jq -r .verifyUrl | sed 's/.*code=//')"
PANEL=(--resolve panel.hotel.test:443:"$SERVER_IP" -k -H 'X-TM-Request: 1' -H 'Content-Type: application/json')
[[ "$(curl -s -o /dev/null -w '%{http_code}' "${PANEL[@]}" -X POST https://panel.hotel.test/api/panel-address/verify --data "{\"code\":\"$CODE\"}")" == "204" ]] \
    || fail "address check through nginx on the new domain failed"
pass "panel reachable on panel.hotel.test and verified"

TOKEN_URL="$(body_of "$(call POST /api/panel-address/handover)" | jq -r .url)"
NEW_JAR="$(mktemp)"
[[ "$(curl -s -o /dev/null -w '%{http_code}' "${PANEL[@]}" -c "$NEW_JAR" -b "$NEW_JAR" -X POST https://panel.hotel.test/api/auth/handover \
    --data "{\"token\":\"${TOKEN_URL##*token=}\"}")" == "204" ]] || fail "handover sign-in failed"
[[ "$(curl -s -o /dev/null -w '%{http_code}' "${PANEL[@]}" -b "$NEW_JAR" https://panel.hotel.test/api/auth/me)" == "200" ]] || fail "should be signed in on the new address"
pass "signed in on the new address with a one-time link"

response="$(curl -s "${PANEL[@]}" -c "$NEW_JAR" -b "$NEW_JAR" -X POST https://panel.hotel.test/api/panel-address/close-setup)"
PENDING="$(jq -r .pending.id <<<"$response")"
[[ "$PENDING" != "null" ]] || fail "closing the setup port should need confirmation: $response"
[[ "$(curl -s "${PANEL[@]}" -b "$NEW_JAR" -X POST "https://panel.hotel.test/api/firewall/changes/$PENDING/confirm" | jq -r .status)" == "confirmed" ]] \
    || fail "confirming from the new address failed"
if curl -sk --max-time 5 -o /dev/null https://server:8443/api/health; then
    fail "the setup address should be closed"
fi
pass "setup address closed (nginx + firewall), confirmed from the new address"

echo "Cloudflare passed."

