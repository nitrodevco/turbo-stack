#!/usr/bin/env bash
# Turbo Manager installer.
#
#   curl -fsSL https://<install-domain>/install.sh | sudo bash
#
# Options:
#   --channel <stable|beta>   Release channel (default: stable)
#   --base-url <url>          Install domain to download from (default: $TM_BASE_URL or the official domain)
#   --bundle <file.tar.gz>    Install from a local bundle instead of downloading
#   --public-host <host|ip>   Address printed in the setup link (default: auto-detected)
#   --no-upgrade              Skip `apt-get upgrade` (faster; used by tests)
#   --no-root-terminal        Permanently disable the panel's root terminal feature
#   --force                   Continue on unsupported OS versions
set -euo pipefail

# Keep in sync with src/TurboManager.Core/Branding.cs and ManagerPaths.cs.
PRODUCT_NAME="Turbo Manager"
SLUG="turbo-manager"
SHORT="tm"
SYSTEM_USER="$SLUG"
INSTALL_ROOT="/opt/$SLUG"
STATE_ROOT="/var/lib/$SLUG"
LOG_ROOT="/var/log/$SLUG"
CONFIG_ROOT="/etc/$SLUG"
PANEL_PORT=7800
SETUP_PORT=8443
DEFAULT_BASE_URL="https://get.turbo-manager.invalid" # TODO: replace once the install domain is chosen.

CHANNEL="stable"
BASE_URL="${TM_BASE_URL:-$DEFAULT_BASE_URL}"
BUNDLE=""
PUBLIC_HOST=""
UPGRADE=1
ROOT_TERMINAL=1
FORCE=0

log() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die() { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --channel) CHANNEL="$2"; shift 2 ;;
        --base-url) BASE_URL="${2%/}"; shift 2 ;;
        --bundle) BUNDLE="$2"; shift 2 ;;
        --public-host) PUBLIC_HOST="$2"; shift 2 ;;
        --no-upgrade) UPGRADE=0; shift ;;
        --no-root-terminal) ROOT_TERMINAL=0; shift ;;
        --force) FORCE=1; shift ;;
        -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
        *) die "Unknown option: $1" ;;
    esac
done

# ---------------------------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------------------------
[[ $EUID -eq 0 ]] || die "Run as root (sudo bash)."
[[ -d /run/systemd/system ]] || die "systemd is not running; $PRODUCT_NAME needs a systemd-based Ubuntu server."

# shellcheck disable=SC1091
. /etc/os-release
if [[ "${ID:-}" != "ubuntu" || ! "${VERSION_ID:-}" =~ ^(24\.04|26\.04)$ ]]; then
    [[ $FORCE -eq 1 ]] || die "Unsupported OS: ${PRETTY_NAME:-unknown}. Ubuntu 24.04 or 26.04 LTS required (--force to try anyway)."
    warn "Unsupported OS ${PRETTY_NAME:-unknown}; continuing because of --force."
fi

case "$(uname -m)" in
    x86_64) ARCH="x64" ;;
    aarch64|arm64) ARCH="arm64" ;;
    *) die "Unsupported CPU architecture: $(uname -m)" ;;
esac

IN_CONTAINER=0
if systemd-detect-virt --container --quiet 2>/dev/null || [[ -f /.dockerenv ]]; then
    IN_CONTAINER=1
    warn "Running inside a container: swap and some kernel-level steps are skipped."
fi

MEM_MB=$(( $(awk '/MemTotal/ {print $2}' /proc/meminfo) / 1024 ))
DISK_GB=$(( $(df -Pk / | awk 'NR==2 {print $4}') / 1024 / 1024 ))
(( MEM_MB >= 1900 )) || warn "Only ${MEM_MB} MB RAM. 2 GB+ is recommended (4 GB+ for building the Nitro client)."
(( DISK_GB >= 20 )) || warn "Only ${DISK_GB} GB free disk. 20 GB+ is recommended."

if [[ -f "$INSTALL_ROOT/panel/tm-panel" ]]; then
    log "$PRODUCT_NAME is already installed; upgrading in place (settings and data are kept)."
fi

# ---------------------------------------------------------------------------------------------
# Packages
# ---------------------------------------------------------------------------------------------
export DEBIAN_FRONTEND=noninteractive
log "Updating package lists"
apt-get update -qq
if [[ $UPGRADE -eq 1 ]]; then
    log "Upgrading installed packages"
    apt-get -y -qq -o Dpkg::Options::=--force-confold upgrade
fi
log "Installing base packages"
# .NET apps (Turbo) need ICU; the package carries the version in its name (libicu74 on 24.04).
ICU_PACKAGE="$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n1)"
apt-get -y -qq install --no-install-recommends \
    ca-certificates curl git unzip jq openssl acl iproute2 rsync xz-utils ${ICU_PACKAGE} \
    nginx ufw fail2ban python3-systemd unattended-upgrades >/dev/null

# ---------------------------------------------------------------------------------------------
# Swap (the Nitro client build needs ~2 GB of memory)
# ---------------------------------------------------------------------------------------------
if [[ $IN_CONTAINER -eq 0 && $MEM_MB -lt 4000 && -z "$(swapon --show --noheadings)" ]]; then
    log "Creating a 2 GB swap file"
    fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

# ---------------------------------------------------------------------------------------------
# Bundle
# ---------------------------------------------------------------------------------------------
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

if [[ -z "$BUNDLE" ]]; then
    log "Fetching $CHANNEL release information from $BASE_URL"
    curl -fsSL "$BASE_URL/channels/$CHANNEL.json" -o "$WORK_DIR/release.json" || die "Could not download release information."
    VERSION="$(jq -r '.version' "$WORK_DIR/release.json")"
    ARTIFACT_URL="$(jq -r --arg a "$ARCH" '.artifacts[$a].url' "$WORK_DIR/release.json")"
    ARTIFACT_SHA="$(jq -r --arg a "$ARCH" '.artifacts[$a].sha256' "$WORK_DIR/release.json")"
    [[ "$ARTIFACT_URL" != "null" ]] || die "No $ARCH build in the $CHANNEL channel."
    [[ "$ARTIFACT_URL" =~ ^https?:// ]] || ARTIFACT_URL="$BASE_URL/$ARTIFACT_URL"

    log "Downloading $PRODUCT_NAME $VERSION ($ARCH)"
    BUNDLE="$WORK_DIR/bundle.tar.gz"
    curl -fsSL "$ARTIFACT_URL" -o "$BUNDLE" || die "Download failed: $ARTIFACT_URL"
    echo "$ARTIFACT_SHA  $BUNDLE" | sha256sum --check --quiet || die "Checksum mismatch for the downloaded bundle."
    # TODO(M8): verify the release signature (minisign) with the embedded public key.
fi

[[ -f "$BUNDLE" ]] || die "Bundle not found: $BUNDLE"
mkdir -p "$WORK_DIR/bundle"
tar -xzf "$BUNDLE" -C "$WORK_DIR/bundle"
VERSION="$(cat "$WORK_DIR/bundle/VERSION")"

# ---------------------------------------------------------------------------------------------
# User, directories, binaries
# ---------------------------------------------------------------------------------------------
if ! id "$SYSTEM_USER" >/dev/null 2>&1; then
    log "Creating system user $SYSTEM_USER"
    useradd --system --home-dir "$STATE_ROOT" --no-create-home --shell /usr/sbin/nologin "$SYSTEM_USER"
fi

install -d -m 0755 "$INSTALL_ROOT" "$CONFIG_ROOT"
install -d -m 0750 -o "$SYSTEM_USER" -g "$SYSTEM_USER" "$STATE_ROOT" "$LOG_ROOT"
install -d -m 0700 "$CONFIG_ROOT/tls"

systemctl stop "$SHORT-panel.service" "$SHORT-agent.service" 2>/dev/null || true

log "Installing $PRODUCT_NAME $VERSION into $INSTALL_ROOT"
for component in panel agent; do
    rm -rf "${INSTALL_ROOT:?}/$component.new"
    cp -a "$WORK_DIR/bundle/$component" "$INSTALL_ROOT/$component.new"
    rm -rf "${INSTALL_ROOT:?}/$component"
    mv "$INSTALL_ROOT/$component.new" "$INSTALL_ROOT/$component"
done
chown -R root:root "$INSTALL_ROOT"
chmod 0755 "$INSTALL_ROOT/panel/tm-panel" "$INSTALL_ROOT/agent/tm-agent"
echo "$VERSION" > "$INSTALL_ROOT/VERSION"

install -m 0755 "$WORK_DIR/bundle/bin/"* /usr/local/sbin/
install -m 0644 "$WORK_DIR/bundle/systemd/$SHORT-agent.service" /etc/systemd/system/
install -m 0644 "$WORK_DIR/bundle/systemd/$SHORT-panel.service" /etc/systemd/system/

# ---------------------------------------------------------------------------------------------
# Public address for the setup link
# ---------------------------------------------------------------------------------------------
if [[ -z "$PUBLIC_HOST" ]]; then
    PUBLIC_HOST="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i <= NF; i++) if ($i == "src") { print $(i + 1); exit }}')"
    PUBLIC_HOST="${PUBLIC_HOST:-$(hostname -I | awk '{print $1}')}"
fi

cat > "$CONFIG_ROOT/install.env" <<EOF
# Written by install.sh — read by tm-setup-token and future self-updates.
TM_VERSION=$VERSION
TM_CHANNEL=$CHANNEL
TM_BASE_URL=$BASE_URL
TM_PUBLIC_HOST=$PUBLIC_HOST
TM_SETUP_PORT=$SETUP_PORT
TM_PANEL_PORT=$PANEL_PORT
TM_ROOT_TERMINAL=$ROOT_TERMINAL
TM_IN_CONTAINER=$IN_CONTAINER
EOF
chmod 0644 "$CONFIG_ROOT/install.env"

# ---------------------------------------------------------------------------------------------
# nginx: temporary setup entry point on https://<ip>:8443 with a self-signed certificate.
# The panel closes this once it has moved to its own domain.
# ---------------------------------------------------------------------------------------------
if [[ ! -f "$CONFIG_ROOT/tls/setup.crt" ]]; then
    log "Creating a self-signed certificate for the setup address"
    if [[ "$PUBLIC_HOST" =~ ^[0-9.]+$ ]]; then
        SAN="DNS:localhost,IP:$PUBLIC_HOST"
    else
        SAN="DNS:localhost,DNS:$PUBLIC_HOST"
    fi
    openssl req -x509 -nodes -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -days 825 \
        -subj "/CN=$PRODUCT_NAME setup" -addext "subjectAltName=$SAN" \
        -keyout "$CONFIG_ROOT/tls/setup.key" -out "$CONFIG_ROOT/tls/setup.crt" 2>/dev/null
    chmod 0600 "$CONFIG_ROOT/tls/setup.key"
fi

# http-level helpers every panel-generated site relies on.
cat > "/etc/nginx/conf.d/$SHORT-maps.conf" <<EOF
# Managed by $PRODUCT_NAME.
map \$http_upgrade \$tm_connection_upgrade {
    default upgrade;
    ''      close;
}
EOF

# Webroot for ACME (Let's Encrypt) HTTP-01 challenges, written by tm-agent.
install -d -m 0755 /var/lib/turbo-manager-acme

# The setup site is kept as a template so tm-panel-recover can reopen it later.
cat > "$CONFIG_ROOT/setup-site.conf" <<EOF
# Managed by $PRODUCT_NAME — temporary setup entry point.
server {
    listen $SETUP_PORT ssl http2;
    listen [::]:$SETUP_PORT ssl http2;
    server_name _;

    ssl_certificate     $CONFIG_ROOT/tls/setup.crt;
    ssl_certificate_key $CONFIG_ROOT/tls/setup.key;
    ssl_protocols       TLSv1.2 TLSv1.3;

    client_max_body_size 100m;

    location / {
        proxy_pass http://127.0.0.1:$PANEL_PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$tm_connection_upgrade;
        proxy_read_timeout 1h;
    }
}
EOF

# The setup address is closed once the panel has moved to its own domain; upgrades keep it closed.
if [[ ! -f "$CONFIG_ROOT/setup-closed" ]]; then
    cp "$CONFIG_ROOT/setup-site.conf" "/etc/nginx/sites-available/$SHORT-setup.conf"
    ln -sf "/etc/nginx/sites-available/$SHORT-setup.conf" "/etc/nginx/sites-enabled/$SHORT-setup.conf"
fi
nginx -t -q || die "nginx configuration test failed."
systemctl enable --now nginx >/dev/null 2>&1
systemctl reload nginx

# ---------------------------------------------------------------------------------------------
# Firewall + brute-force protection
# ---------------------------------------------------------------------------------------------
# Only on first install: afterwards the panel owns the firewall, and re-running the
# installer (upgrades) must never undo rules made in the panel or by hand.
if [[ ! -f "$CONFIG_ROOT/firewall-initialized" ]]; then
    log "Configuring the firewall"
    if ufw default deny incoming >/dev/null \
        && ufw default allow outgoing >/dev/null \
        && ufw limit 22/tcp comment "tm:ssh" >/dev/null \
        && ufw allow 80/tcp comment "tm:http" >/dev/null \
        && ufw allow 443/tcp comment "tm:https" >/dev/null \
        && ufw allow "$SETUP_PORT/tcp" comment "tm:setup" >/dev/null \
        && ufw --force enable >/dev/null; then
        touch "$CONFIG_ROOT/firewall-initialized"
        log "Firewall enabled (22 rate-limited, 80, 443, $SETUP_PORT open)"
    else
        warn "Could not configure ufw (missing NET_ADMIN in a container?). The panel will report this."
    fi
fi

# Failed panel sign-ins are written here by tm-panel; the panel jail bans their source.
install -m 0640 -o "$SYSTEM_USER" -g adm /dev/null "$LOG_ROOT/auth.log" 2>/dev/null || touch "$LOG_ROOT/auth.log"

cat > /etc/fail2ban/filter.d/$SLUG-panel.conf <<'EOF'
# Managed by Turbo Manager: failed sign-ins to the panel.
[Definition]
failregex = ^\S+ AUTH_FAILURE ip=<HOST>
ignoreregex =
EOF

# Jail settings are edited from the panel afterwards, so only write them on first install.
if [[ ! -f /etc/fail2ban/jail.d/$SLUG.conf ]]; then
    cat > /etc/fail2ban/jail.d/$SLUG.conf <<EOF
# Managed by $PRODUCT_NAME.

[DEFAULT]
backend = systemd

[sshd]
enabled = true

[$SLUG-panel]
enabled = true
backend = auto
filter = $SLUG-panel
logpath = $LOG_ROOT/auth.log
port = http,https,$SETUP_PORT
maxretry = 10
findtime = 600
bantime = 3600
EOF
fi
systemctl enable --now fail2ban >/dev/null 2>&1 || warn "fail2ban could not be started."
systemctl restart fail2ban >/dev/null 2>&1 || true

cat > /etc/logrotate.d/$SLUG <<EOF
$LOG_ROOT/*.log {
    weekly
    rotate 8
    compress
    delaycompress
    missingok
    notifempty
    copytruncate
}
EOF

cat > /etc/apt/apt.conf.d/20auto-upgrades <<EOF
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF

# ---------------------------------------------------------------------------------------------
# Services
# ---------------------------------------------------------------------------------------------
log "Starting services"
systemctl daemon-reload
systemctl enable --now "$SHORT-agent.service" "$SHORT-panel.service" >/dev/null 2>&1

for _ in $(seq 1 60); do
    curl -fsS "http://127.0.0.1:$PANEL_PORT/api/health" >/dev/null 2>&1 && break
    sleep 1
done
curl -fsS "http://127.0.0.1:$PANEL_PORT/api/health" >/dev/null 2>&1 \
    || die "The panel did not start. Check: journalctl -u $SHORT-panel -n 100"

# ---------------------------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------------------------
if [[ "$(curl -fsS "http://127.0.0.1:$PANEL_PORT/api/setup/status" | jq -r '.claimed')" == "true" ]]; then
    log "$PRODUCT_NAME $VERSION is installed and already set up."
    exit 0
fi

SETUP_URL="$(/usr/local/sbin/tm-setup-token --url-only)"

printf '\n\033[1;32m✔ %s %s installed.\033[0m\n\n' "$PRODUCT_NAME" "$VERSION"
printf '  Open this link to finish setup (valid for 24 hours):\n\n'
printf '    \033[1m%s\033[0m\n\n' "$SETUP_URL"
printf '  Your browser will warn about the certificate: that is expected for this one-time\n'
printf '  setup address. The panel moves to your own domain during setup.\n\n'
printf '  Lost the link? Run: sudo tm-setup-token\n\n'
