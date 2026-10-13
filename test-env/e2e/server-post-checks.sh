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

# Where Ubuntu's MySQL is out of support (8.0 on 24.04) the panel must have recommended MariaDB.
# (Captured first: with pipefail, "apt-cache | grep -q" fails when grep exits early.)
MYSQL_POLICY="$(apt-cache policy mysql-server)"
if grep -qE 'Candidate: 8\.0\.' <<<"$MYSQL_POLICY"; then
    dpkg-query -W -f='${db:Status-Status}' mariadb-server 2>/dev/null | grep -q '^installed$' \
        || fail "MariaDB should have been recommended and installed next to an out-of-support MySQL"
    pass "MariaDB recommended and installed (Ubuntu's MySQL is 8.0)"
else
    dpkg-query -W -f='${db:Status-Status}' mysql-server 2>/dev/null | grep -q '^installed$' || fail "MySQL should have been recommended"
    pass "MySQL recommended and installed (Ubuntu's MySQL is supported)"
fi

SQLCLIENT="$(command -v mariadb || command -v mysql)"
[[ "$("$SQLCLIENT" -uturbo -pE2e-Mysql-Passw0rd -h 127.0.0.1 turbo -N -e 'SELECT COUNT(*) FROM players' 2>/dev/null)" == "2" ]] \
    || fail "the turbo user should log in over TCP and see the restored rows"
if "$SQLCLIENT" -uturbo -pE2e-Mysql-Passw0rd -h 127.0.0.1 mysql -N -e 'SELECT 1' >/dev/null 2>&1; then
    fail "the turbo user must not reach other databases"
fi
pass "app user can log in, sees its restored data, and nothing else"

[[ "$(PGPASSWORD=E2e-Postgres-Passw0rd psql -h 127.0.0.1 -U studio -d studio -tAc 'SELECT current_user')" == "studio" ]] \
    || fail "the studio user should log in to PostgreSQL"
if PGPASSWORD=E2e-Postgres-Passw0rd psql -h 127.0.0.1 -U studio -d other_app -tAc 'SELECT 1' >/dev/null 2>&1; then
    fail "the studio user must not connect to another app's database"
fi
pass "PostgreSQL app user can log in to its database and not to another app's"

[[ "$(stat -c '%a' /var/lib/turbo-manager-backups/databases)" == "700" ]] || fail "backup directory must be root-only"
pass "database backups are root-only"

[[ "$(systemctl show -p User --value tm-app-api.service)" == "hotel" ]] || fail "the api service should run as the app user"
[[ "$(ps -o user= -p "$(systemctl show -p MainPID --value tm-app-api.service)")" == "hotel" ]] || fail "the api process should run as hotel"
id -nG hotel | grep -qw tm-apps || fail "hotel should be in tm-apps"
if id -nG hotel | grep -qwE 'sudo|adm|turbo-manager'; then fail "the app user must not be in privileged groups"; fi
pass "app service runs as its own unprivileged user"

[[ "$(stat -c '%a %U' /home/hotel)" == "750 hotel" ]] || fail "the app home should be 750 and owned by hotel"
KEYS="$(find /home/hotel/.ssh/tm -type f ! -name '*.pub' -printf '%m %u\n' | sort -u)"
[[ "$KEYS" == "600 hotel" ]] || fail "deploy keys should be 600 and owned by hotel (got: $KEYS)"
[[ -z "$(find /home/hotel/apps ! -user hotel -print -quit)" ]] || fail "nothing under the apps directory should belong to another user"
[[ "$(stat -c '%U' /etc/turbo-manager/apps/api.run)" == "root" ]] || fail "the service launcher must be root-owned"
pass "app files: home 750, private keys 600, launcher root-owned"

# Like an SFTP upload as the app user: new files must be readable by nginx without any extra step.
runuser -u hotel -- sh -c 'umask 077 && mkdir -p ~/sites/assets/bundled && echo nitro-bundle > ~/sites/assets/bundled/figure.nitro'
ASSET="$(curl -s -i -H 'Host: assets.hotel.test' http://127.0.0.1/bundled/figure.nitro)"
[[ "$ASSET" == *"200 OK"* && "$ASSET" == *"nitro-bundle"* ]] || fail "an uploaded asset should be served: $ASSET"
[[ "$ASSET" == *"Access-Control-Allow-Origin: *"* && "$ASSET" == *"max-age=2592000"* ]] || fail "assets need CORS and a long cache: $ASSET"
[[ "$(stat -c '%U' /home/hotel/sites/assets)" == "hotel" ]] || fail "the assets folder should belong to the app user"
pass "assets folder: uploads by the app user are served with CORS and caching"

[[ "$(stat -c '%U' /home/hotel/notes/readme.txt /home/hotel/notes/blob.bin | sort -u)" == "hotel" ]] \
    || fail "files written through the file manager must belong to the app user"
pass "file manager writes as the app user (never root)"

runuser -u hotel -- test -w /home/hotel/sites/assets/bundled/upload.nitro || fail "the app user should be able to manage what the SFTP account uploaded"
[[ "$(stat -c '%U %a' /srv/tm-sftp/assets-upload)" == "root 755" ]] || fail "the SFTP chroot must belong to root"
[[ "$(getent passwd assets-upload | cut -d: -f7)" == "/usr/sbin/nologin" ]] || fail "the SFTP account must have no shell"
pass "SFTP: chroot root-owned, no shell, uploads usable by the app user"

[[ "$(stat -c '%a %U' /var/backups/turbo-manager)" == "700 root" && -f /var/backups/turbo-manager/config ]] || fail "the local repository must be root-only"
[[ "$(stat -c '%a' /etc/turbo-manager/backup-keys)" == "700" ]] || fail "backup keys must be root-only"
[[ -z "$(ls -A /var/lib/turbo-manager-backups/staging 2>/dev/null)" && ! -e /var/lib/turbo-manager/backup/panel.db ]] || fail "backup staging must be cleaned up"
[[ "$(stat -c '%U' /home/hotel/restored-*)" == "hotel" ]] || fail "restored folders belong to the app user"
[[ "$(stat -c '%a' /var/lib/turbo-manager-backups/restores/panel-before-restore-*)" == "700" ]] || fail "the panel state from before a restore is kept, root-only"
[[ "$(stat -c '%U %a' /var/lib/turbo-manager/panel.db)" == "turbo-manager 600" ]] || fail "the restored panel database belongs to the panel"
pass "backups: local repository and keys root-only, staging cleaned up, restored folder handed to the app user; panel state from before a restore kept"

[[ -z "$(find /opt/turbo-manager/runtimes ! -user root -print -quit)" ]] || fail "runtimes must be owned by root"
[[ -z "$(find /opt/turbo-manager/runtimes ! -type l -perm /022 -print -quit)" ]] || fail "runtimes must not be group/world writable"
pass "runtimes are root-owned and not writable by apps"

echo "Server post-checks passed."
