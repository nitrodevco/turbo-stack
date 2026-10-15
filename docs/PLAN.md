# Turbo Stack — Plan

A self-hosted Ubuntu server panel (in the spirit of Ploi and AMP) built first-class for the
**Turbo** emulator (`turbo-cloud`) and the **Nitro** client stack (`nitro-next`), while still
being a complete general-purpose server manager.

**Scope decisions (confirmed):**
- **Single server** — the panel manages the box it is installed on (AMP-style).
- **nitro-studio is out of scope** for this work.
- **MySQL by default where Ubuntu ships a supported MySQL (8.4+, e.g. 26.04); MariaDB where it
  doesn't (24.04 ships MySQL 8.0, past Oracle's EOL)** — the panel recommends per server; any
  engine can still be chosen, plus **PostgreSQL** (see 6.2 for Turbo's
  engine constraint).
- **Cloudflare is the default edge** — the panel guides the user through creating an API token
  and then manages DNS, SSL mode, cache purge, etc. (section 6.8).
- **Repos may be private** — deploy keys / tokens are a first-class path, not an afterthought.
- **Firewall management** is a full feature area (section 6.9).

**North star:** fresh Ubuntu box → run one install script → open the panel URL → answer a short
setup wizard → a fully wired hotel (client, imager, emulator, admin, website, assets, TLS,
database, daemons, auto-deploy) is live in minutes. Every generated piece stays visible and
customizable afterwards.

---

## 1. What we are deploying (ground truth from the repos)

### turbo-cloud (`D:\Repositories\turbo\turbo-workspace\turbo-cloud`)
| Item | Facts that matter to the manager |
|---|---|
| Runtime | .NET 10 (`global.json` SDK 10.0.400). Single executable: `Turbo.Main`. |
| Build | `dotnet publish Turbo.Main/Turbo.Main.csproj -c Release -o <release>` |
| Run | `cd <release> && dotnet Turbo.Main.dll` (cwd = content root). SIGTERM = clean shutdown. |
| Migrate | `dotnet Turbo.Main.dll migrate [--status]` (status exit: 0 ok / 2 behind / 1 error). Auto-migrates on start by default. |
| Config | `appsettings*.json` + env vars `TURBO__Section__Key`; SuperSocket listener keys unprefixed (`serverOptions__WebSocketServer__listeners__0__port`). `DOTNET_ENVIRONMENT`. |
| Ports | WS 9001 (no TLS — nginx terminates), TCP 30000 (optional), Admin API 8090, Web API 8092, Gamedata 8094, Orleans 11111/3000 (**binds all interfaces → must be firewalled**). |
| DB | MySQL 8 / MariaDB 10.6+. EF migrations + idempotent seeds; no dump needed. |
| Secrets | `Turbo:Crypto:PublicKey/PrivateKey` (defaults are public!), `Turbo:Assets:SecretKey`, Discord OAuth. |
| State dirs | `assets/`, `camera/`, `camera/cache`, plugins folder — must live **outside** release dirs. |
| First admin | `Turbo:Owner:Name` / `DiscordId` → setup link printed to log; or console `adminsetup <name>`. |
| Frontends | `turbo-admin`, `turbo-web` (git submodules): static Vite SPAs, Yarn 4 + Node 22+, `dist/`, `/api/` proxied to 8090 / 8092. Admin needs HTTPS + fixed domain (passkeys). |
| Existing kit | `scripts/ploi/` (atomic releases, `.env` → config mapping in `lib.sh`, nginx snippets). We port this logic, not reinvent it. |

### nitro-next (`D:\Repositories\nitro-next`)
| Item | Facts that matter to the manager |
|---|---|
| Tooling | Yarn 4.15 via Corepack, workspaces, `.yarn/patches` must ship. **Node 24.** |
| nitro-react | Static SPA. `yarn install --immutable && yarn build-client` → `packages/nitro-react/dist`. Build needs ~2 GB RAM (swap!). `VITE_SOCKET_URL` read from **repo-root** `.env`. Runtime config URL is **hardcoded** in `index.html` (`window.NitroConfig['nitro.config.url']`). |
| nitro-imager | Long-running Node (Hono) via `tsx src/main.ts`, no build. Port 3030 on 127.0.0.1. Must run inside the installed monorepo. Env: `IMAGER_*` (templates/figure/figuremap/figuredata/badge URLs or local paths — missing path = startup failure). Memory-only cache (~800 MB at 300 libs). `/health`. |
| nitro-studio | **Out of scope.** (Separate repo; would need Node 24 + Postgres. The assets host still accepts SFTP uploads, so an externally run Studio can publish into it.) |
| Static asset host | Bundles (`bundled/<kind>/*.nitro`), images, gamedata files — plain static + CORS. |
| Missing today | No deploy script / nginx / service unit for client or imager — the manager provides them. |

**Key insight:** these apps reference each other's URLs everywhere (socket URL, imager URL in
client config + turbo-web CSP + `AvatarImageUrl`, gamedata URLs in client + imager, admin
`PanelUrl` for passkeys, `ClientUrl` with `{ticket}`...). Hand-wiring this is the #1 source of
broken installs. The manager's core differentiator is a **wiring graph** that owns these links
(section 5).

---

## 2. Architecture

```
                 Internet
                    │ 80/443 (+ panel port during setup)
               ┌────▼─────┐
               │  nginx   │  vhosts rendered by the panel; TLS via ACME
               └─┬──┬───┬─┘
   static dist/  │  │   └──── reverse proxy → app daemons (127.0.0.1 only)
                 │  │
        ┌────────▼──▼─────────┐   unix socket (gRPC, mTLS-less,   ┌───────────────────┐
        │  ts-panel (web+API) │──── peer-cred checked) ──────────►│ ts-agent (root)   │
        │  user: turbo-stack│                                   │ typed operations  │
        │  SQLite state       │◄── job progress / log streams ────│ only — no "run    │
        └────────┬────────────┘                                   │ arbitrary shell"  │
                 │ SignalR/WebSocket                              └─────────┬─────────┘
            Browser UI (React)                                              │
                                     systemd · apt · nginx · ufw · mysql · postgres ·
                                     useradd · certs · git · builds (as site users)
```

### 2.1 Privilege separation (non-negotiable for a public-facing panel)
- **ts-panel** — ASP.NET Core, runs as unprivileged `turbo-stack` user, listens on
  `127.0.0.1:7800`, fronted by nginx. Holds auth, UI, state, job orchestration.
- **ts-agent** — small root daemon on `/run/turbo-stack/agent.sock` (0660, group
  `turbo-stack`). Exposes a **closed set of typed operations** (`WriteNginxSite`,
  `CreateSystemUser`, `SystemdControl`, `CreateDatabase`, `RunBuildAsUser`...). Every input is
  validated (domain regex, path must be under managed roots, usernames from allow-pattern).
  A compromised panel cannot get an arbitrary root shell except via the explicitly
  permission-gated "root terminal" feature (which can be disabled at install time).
- Builds and app processes **never** run as root — always as the owning site user.

### 2.2 Tech stack (matches the user's existing stack)
| Layer | Choice | Why |
|---|---|---|
| Backend | .NET 10 ASP.NET Core, published **self-contained single-file** | Same language as Turbo; AMP is C#; self-contained means panel never breaks when the server's .NET SDK is upgraded for Turbo. |
| Panel DB | SQLite (EF Core) | Zero-ops, trivially backed up. |
| Realtime | SignalR | Live logs, job progress, metrics. |
| Agent IPC | gRPC over unix socket | Typed contract, streaming for logs/builds. |
| Frontend | React 19 + Vite + TypeScript + Tailwind + shadcn/ui + TanStack Query/Router | Same as turbo-admin/turbo-web. Embedded into the panel binary. |
| Terminal / editor | xterm.js, Monaco | Web terminal, config/file editing with diff. |
| Process mgmt | **systemd** (not Supervisor) | cgroup resource limits, sandboxing, journald logs, socket/FIFO stdin for emulator console. |
| TLS | Cloudflare Origin CA certs (default) + built-in ACME (Certes lib) HTTP-01/DNS-01 fallback | No certbot coupling; works proxied or not; panel tracks expiry. |
| Cloudflare | Typed client over the Cloudflare v4 REST API | DNS, zone settings, cache purge, origin certs, IP ranges. |
| Firewall | UFW (managed rule set) + fail2ban | Familiar to admins who SSH in; panel rules are tagged so manual rules coexist. |

### 2.3 Repo layout
```
turbo-stack/
  install/install.sh            # the one-liner bootstrap
  src/TurboManager.Panel/       # web host, API, auth, jobs
  src/TurboManager.Agent/       # root daemon
  src/TurboManager.Contracts/   # gRPC protos + shared DTOs
  src/TurboManager.Core/        # domain: servers, sites, apps, recipes, wiring graph
  src/TurboManager.Recipes/     # built-in recipe manifests + templates (nginx, systemd, env)
  web/                          # React UI
  tests/                        # unit + integration + e2e (Docker-based, section 8)
  test-env/                     # Docker images, compose file, fakes (section 8)
  docs/
```

---

## 3. Install script

`curl -fsSL https://install.nitrodev.co/install.sh | sudo bash`

**Distribution** (product name **Turbo Stack**, slug `turbo-stack`, short prefix `ts`; the
name lives in one constants file + one env var so a rename stays cheap):
- **Install domain** (`install.nitrodev.co`, on Cloudflare): serves `install.sh` (and a
  `install.sh.sha256` / signature), a `latest.json` per channel (`stable`, `beta`) with
  version, per-arch download URLs and checksums, and a short landing page with the one-liner.
- **Binaries** are built by GitHub Actions and attached to **GitHub Releases**; the install
  domain only points at them (no binary hosting of our own). Artifacts are signed
  (minisign/cosign); the public key is embedded in `install.sh` and in the panel, so
  self-updates are verified the same way.
- Panel self-update polls `latest.json` for its channel.
- Install script is also usable offline: `install.sh --bundle ts-x64.tar.gz`.

Deliberately small; heavy lifting happens in the wizard where the user can watch it.

1. Preflight: Ubuntu **24.04 or 26.04 LTS**, x64/arm64, root, ≥2 GB RAM (warn), ≥20 GB disk,
   no existing nginx/panel (or `--force`).
2. `apt update && upgrade`; base packages: `curl git unzip ufw fail2ban unattended-upgrades
   acl nginx`.
3. Create `turbo-stack` system user + dirs: `/opt/turbo-stack` (binaries),
   `/var/lib/turbo-stack` (state), `/var/log/turbo-stack`.
4. Swap file if RAM < 4 GB (nitro-react build needs ~2 GB).
5. Firewall: allow 22, 80, 443, setup port (8443); default deny. Enable fail2ban (sshd + panel jail).
6. Download panel + agent release for the arch, verify SHA-256 + signature, install systemd
   units (`ts-agent.service`, `ts-panel.service`).
7. nginx: default server on **:8443 with a self-signed cert** proxying to the panel.
8. Generate a **one-time setup token** (stored hashed) and print:
   ```
   ✔ Turbo Stack installed.
   Open: https://203.0.113.10:8443/setup?token=7Gk2-...   (expires in 24h)
   ```
   The token stops anyone who finds the IP first from claiming the panel.

Flags: `--panel-domain`, `--no-root-terminal`, `--channel stable|beta`, `--unattended
--config hotel.yaml` (headless install from a saved answers file — great for rebuilding).

---

## 4. Setup wizard (first run)

Each step is skippable/revisable; the end is a review screen, then one install job graph.

The wizard is always reached at **`https://<server-ip>:8443/setup?token=…`** (self-signed
cert; the page explains the browser warning). Moving the panel to its real domain happens
inside the wizard, once Cloudflare is connected.

1. **Claim** — token check → create owner account → enforce 2FA (TOTP or passkey).
2. **Cloudflare & base domain** — guided token creation (section 6.8) → pick the zone
   (`myhotel.com`). "I don't use Cloudflare" → manual DNS path with Let's Encrypt.
3. **Panel address** — default `panel.myhotel.com`, **proxied through Cloudflare**:
   1. panel creates the DNS record + vhost (Origin CA cert, Cloudflare-only firewall rule);
   2. **verifies** it loads end-to-end through Cloudflare, including the realtime
      WebSocket (a test page opened in a new tab reports back to the wizard);
   3. tells the user to continue from the new URL (session handed over via one-time link);
   4. the IP:8443 entry point stays open until setup finishes, then is **closed** (vhost
      removed + firewall rule deleted), with a dashboard notice and the recovery command
      `sudo ts-cli panel expose-ip` (re-opens 8443 temporarily with a fresh token) for when
      DNS/Cloudflare is broken.
   Optional extras: Cloudflare Access (Zero Trust) policy in front of the panel host,
   panel IP allowlist.
4. **Server basics** — hostname, timezone, SSH hardening checks (password auth, root login),
   add owner's SSH public key, automatic security updates on/off.
5. **What are we building?**
   - 🏨 **Turbo + Nitro hotel** (preset)
   - 🧩 Custom (pick recipes individually)
   - 🖥️ Just manage this server
6. **Domains** — one subdomain per role on the chosen zone, every name editable:

   | Role | Default host | Proxied |
   |---|---|---|
   | Panel | `panel.myhotel.com` (set in step 3) | ☁ |
   | Website (turbo-web) | `myhotel.com` (+ `www` redirect) | ☁ |
   | Game client (nitro-react) | `play.myhotel.com` | ☁ |
   | WebSocket (emulator) | `ws.myhotel.com` | ☁ (WebSockets on) |
   | Admin (turbo-admin) | `admin.myhotel.com` | ☁ (own origin needed for passkeys) |
   | Imager | `imager.myhotel.com` | ☁ |
   | Assets + gamedata | `assets.myhotel.com` | ☁ |

   Subdomain-per-role is the only layout (no path-based client). The panel previews the exact
   DNS records it will create (A/AAAA → server IP); existing conflicting records are shown
   with "replace / keep" choices. Non-Cloudflare path: records to create at the registrar +
   live DNS check per row.
7. **Sources** — repos prefilled (turbo-cloud, nitro-next), branch or tag. Panel probes each
   repo anonymously; if it's private, it offers:
   - **Deploy key (default)** — panel generates an ed25519 key per repo, shows it with a copy
     button and a direct link to the repo's *Settings → Deploy keys* page, then "Test access".
   - **GitHub token (optional)** — fine-grained PAT (Contents: read, Administration/Webhooks:
     write) lets the panel add deploy keys and webhooks itself.
   - Submodules (turbo-admin, turbo-web) are checked too; if they're private they get their
     own deploy keys (GitHub deploy keys are per-repo), with the panel writing a per-repo SSH
     config/`GIT_SSH_COMMAND` so each submodule uses the right key.
8. **Database** — MySQL 8.4 (default), MariaDB, or PostgreSQL; only engines compatible with
   the selected recipes are enabled (Turbo: MySQL/MariaDB — see 6.2). Auto-create `turbo`
   db + user with a random password.
9. **Assets** — source for bundles/images/gamedata: (a) use an existing public asset URL,
   (b) upload/download an archive into the `assets.` host, (c) SFTP publish later
   (chrooted upload user is created).
   Imager is validated against whatever is chosen (it fails on missing paths, so we pre-check).
10. **Hotel** — hotel name, registration open, owner username (`TURBO_OWNER_NAME`),
   Discord OAuth (optional), isolation mode (one user for everything vs. user per service).
11. **Review → Install** — job graph with live logs per node:
    `runtimes → db → users → clone → build (parallel) → migrate → render config →
    Cloudflare DNS + zone settings → certs → nginx → firewall (incl. Cloudflare-only 80/443) →
    start services → health checks (through Cloudflare) → fetch admin setup link from log`.
12. **Done** — cards with links (play, admin, website), the admin setup link, the generated
    credentials (shown once, stored encrypted), "what next" checklist.

Target: < 10 minutes on a 4 vCPU box, dominated by `dotnet publish` + Vite build.

---

## 5. Core domain model

```
Server
 ├─ Stacks          (e.g. "My Hotel" — a group created from a preset; owns the wiring graph)
 │   └─ Apps        (instances of Recipes)
 ├─ Repositories    (git source; one repo can feed many apps — nitro-next → client + imager)
 ├─ Apps            (a deployable unit: static site, daemon, or both)
 │   ├─ Releases    (atomic, symlinked)
 │   ├─ Services    (systemd units owned by the app)
 │   ├─ Env / Config(layered: recipe defaults → wiring → user overrides → secrets)
 │   └─ Sites       (domains → nginx vhost → routes to app outputs/ports)
 ├─ Databases / DB users
 ├─ System users (isolation), SSH keys, Firewall rules, Cron jobs, Backups
 └─ Panel users / roles / API tokens / audit log
```

### 5.1 Recipes (app types) — declarative manifests
YAML manifests shipped with the panel (and user-addable). One per deployable thing:
`turbo-emulator`, `turbo-admin`, `turbo-web`, `nitro-client`, `nitro-imager`,
`static-assets`, plus generic: `static-site`, `spa`, `node-app`, `dotnet-app`,
`reverse-proxy`, `php-site` (later), `custom-daemon`.

A manifest declares:
- **source**: repo + subpath, submodules, which workspace.
- **toolchain**: `dotnet@10`, `node@24+corepack`.
- **database**: supported engines + how to express the connection, e.g.
  `engines: [mysql, mariadb]` with a per-engine connection-string template. Adding `postgres`
  to Turbo later is a recipe change only (plus whatever provider switch Turbo adds).
- **build steps** (defaults, editable as a script in the UI).
- **outputs**: static dir (`packages/nitro-react/dist`) and/or service command.
- **ports** it needs (allocated by the panel from a pool, bound to 127.0.0.1).
- **shared paths** that survive releases (`assets/`, `camera/`, `plugins/`, `.env`).
- **health check** (HTTP path, TCP port, or command).
- **exports** — values other apps can consume (`socket.public_url`, `imager.public_url`,
  `gamedata.public_url`, `api.internal_url`...).
- **imports** — what it needs, mapped to config keys.
- **nginx template(s)** and **systemd template**.

Example (abridged):
```yaml
id: nitro-imager
source: { repo: nitro-next, workspace: "@nitrodevco/nitro-imager" }
toolchain: [node@24]
build:
  - corepack yarn install --immutable
service:
  command: corepack yarn workspace @nitrodevco/nitro-imager start
  env:
    IMAGER_HOST: 127.0.0.1
    IMAGER_PORT: "{{ port.http }}"
    IMAGER_TEMPLATES_URL:   "{{ import.assets.bundles_url }}/templates/%libname%.nitro"
    IMAGER_FIGURE_BUNDLE_URL: "{{ import.assets.bundles_url }}/figure/%libname%.nitro"
    IMAGER_FIGUREDATA_URL:  "{{ import.turbo.gamedata_url }}/figuredata_json/0"
    IMAGER_BADGE_PARTS:     "{{ import.turbo.gamedata_url }}/badgeparts_json/0"
  memory_max: 1200M
health: { http: "/health" }
exports:
  public_url: "https://{{ site.primary }}"
  avatar_url: "https://{{ site.primary }}/avatarimage?figure=%figure%"
nginx: reverse-proxy
```

### 5.2 The wiring graph (the "creative" part)
- Each Stack holds a graph: app `exports` → other apps' `imports`.
- The panel renders every derived value: Turbo env (`Admin:PanelUrl`, `Web:SiteUrl`,
  `Web:ClientUrl=https://play.../?sso={ticket}`, `Web:AvatarImageUrl`, `Gamedata:PublicUrl`),
  client `.env` (`VITE_SOCKET_URL=wss://ws...`) and `nitro-config.json` (socket, bundle,
  gamedata, imager URLs), imager env, **turbo-web CSP** (`frame-src`/`img-src` for client and
  imager hosts), CORS on the assets host.
- **Change a domain once** → panel shows a diff of every affected file/vhost and which apps
  need rebuild (client: build-time `VITE_SOCKET_URL`) vs restart (emulator/imager) vs nginx
  reload only → one click applies it.
- A **"Stack map"** UI page draws the graph (domains → nginx → apps → ports → DB), with live
  health colouring. This doubles as the troubleshooting screen ("imager red: figuredata URL
  returned 404").
- Users can override any rendered value; overrides are shown as "pinned" so they're never
  silently clobbered.

---

## 6. Feature areas

### 6.1 Sites & nginx
- Panel owns `/etc/nginx/sites-enabled/ts-*.conf`; never touches unmanaged files (shows them
  read-only as "external").
- **Site types**: static/SPA (`try_files $uri /index.html`, long-cache `/assets/`),
  reverse proxy, **WebSocket proxy** (Upgrade headers, 1h timeouts, buffering off — from
  `turbo-cloud/scripts/ploi/nginx-websocket.conf`), SPA + `/api/` proxy (turbo-admin/web),
  path-combined sites (`/gamedata/` → 8094 on assets host), redirect, custom.
- Generated config is split into: rendered template + **user snippet slots**
  (`before-server`, `server`, `location /`) so customization survives regeneration. Full
  "eject to raw config" option.
- Every write: render → `nginx -t` → atomic swap → reload; on failure auto-restore previous
  version and show the error inline with the offending line highlighted.
- Config history with diffs and one-click revert.
- TLS: ACME per site or wildcard, auto-renew, expiry alerts, HSTS toggle, force-HTTPS,
  HTTP/2 + HTTP/3 option. Cloudflare mode: real-IP ranges auto-updated, origin cert option.
- Security headers presets, basic-auth, IP allow/deny, rate limiting (`limit_req`) presets
  (e.g. for `/api/auth/*`), `client_max_body_size`, gzip/brotli.
- Per-site access/error logs with viewer + simple stats (requests, 4xx/5xx, top paths).
- Custom domains: any extra domain/alias can be attached to any app.

### 6.2 Databases
- **Engine abstraction** (`IDatabaseEngine`: install, configure, create db/user, grant,
  rotate, dump, restore, stats) with three implementations: **MySQL 8.4 (default)**,
  **MariaDB (LTS)**, **PostgreSQL 17**. MySQL and MariaDB are mutually exclusive on one box
  (same port/packages); Postgres can run alongside either.
- **Turbo constraint today:** turbo-cloud uses Pomelo (MySQL provider) with ~180
  MySQL-specific EF migrations, so the `turbo-emulator` recipe declares
  `engines: [mysql, mariadb]`. Postgres support is planned in Turbo; when it lands, the recipe
  adds `postgres` with its connection-string template and the wizard enables it.
- **Engine swap**: choose the engine in the wizard or install another later from the
  Databases page. Moving an *existing* hotel between engines is a guided job:
  backup → stop emulator → create target db → let Turbo migrate the empty schema → copy data
  (MySQL↔MariaDB: dump/restore; MySQL→Postgres: pgloader-based, once Turbo supports it) →
  repoint env → start → verify → keep old db until the user confirms.
- Create/drop databases (utf8mb4 default), users, grants (all / read-only / custom), password
  rotate (and offer to push new creds into the dependent app env + restart).
- Remote access toggle per user (binds + firewall rule + host restriction).
- Sizes, table counts, slow-query toggle, process list, basic tuning presets by RAM.
- Import (upload `.sql`/`.sql.gz`), export, **scheduled backups** (mysqldump/pg_dump,
  compression, retention, local + S3-compatible/SFTP targets), restore with confirmation.
- Turbo-specific: show migration status (`migrate --status`), run `fresh-start.sql` with
  guard rails (stop emulator first, backup first).
- Optional one-click Adminer behind panel auth.
- Root/admin DB creds stored encrypted (panel data-protection key), never displayed by default.

### 6.3 Daemons / services
- All apps run as **systemd units** `ts-<app>.service` rendered from recipe templates:
  `User=`, `WorkingDirectory=` (current release), `EnvironmentFile=`, `Restart=always`,
  `MemoryMax`, `CPUQuota`, `TimeoutStopSec` (Turbo needs graceful SIGTERM), hardening
  (`NoNewPrivileges`, `ProtectSystem=strict`, `ReadWritePaths=` shared dirs, `PrivateTmp`).
- Start/stop/restart/enable, status, uptime, restart count, crash loop detection + alert.
- **Emulator console**: unit uses a FIFO/socket for stdin so the panel can send console
  commands (`adminsetup`, etc.) and stream output — AMP-style console tab.
- Scheduled restarts (e.g. daily 05:00) with optional pre-restart hook.
- Generic "custom daemon" for anything else.
- Cron/scheduled tasks UI (per system user, systemd timers under the hood).

### 6.4 Git & deployments
- **GitHub only (v1).** The git layer is an `IGitProvider` interface with one implementation,
  so other providers could be added later without touching the pipeline.
- Repos: public HTTPS, or private via **per-repo deploy keys** (default) or a fine-grained
  GitHub token. Keys live in the owning site user's `~/.ssh/ts/<repo>` (0600), used through
  a per-repo `GIT_SSH_COMMAND`, so isolated users can't read each other's keys. github.com
  host keys pinned (refreshed from GitHub's `/meta` API).
- With a token: repo/branch pickers, auto-add deploy keys + webhooks, show the latest commits
  and an "update available" badge per app, and a link from each deploy to its commit on GitHub.
- Submodules (turbo-admin/turbo-web) resolved with their own credentials; branch/tag selection.
- "Repository health" panel: last fetch, credential test, webhook last delivery + status.
- **Auto-deploy**: GitHub `push` webhook per repo (`https://panel.myhotel.com/hooks/github/<id>`)
  with `X-Hub-Signature-256` HMAC verification and delivery-ID replay protection; branch
  filter; optional "only if paths changed" filter (e.g. nitro-next push touching only
  `packages/nitro-imager` redeploys only the imager; falls back to `git diff` between SHAs
  when the payload's file list is truncated). The hook path is excluded from Cloudflare
  Access / panel IP allowlist and cache. Optional "deploy on GitHub release published"
  trigger instead of every push.
- **Pipeline** (Ploi-style, ported from `scripts/ploi/deploy.sh`):
  1. fetch → checkout into `releases/<ts>-<sha>`
  2. link shared paths (`.env`, `assets`, `camera`, `plugins`)
  3. build as site user with pinned toolchain (logs streamed)
  4. pre-activate hooks (Turbo: `migrate`; or `migrate --status` gate if auto-migrate off)
  5. atomic symlink swap `current → release`
  6. restart/reload services, nginx reload if needed
  7. health check; **auto-rollback** to previous release on failure
  8. prune to N releases (default 5)
- Shared monorepo builds: one nitro-next checkout per release, client + imager both consume it
  (no double install).
- Deploy script editable per app (defaults from recipe), env editor with secret masking.
- Manual deploy, redeploy, **rollback to any kept release**, deploy history with commit
  messages, author, duration, logs.
- Emulator-aware: "deploy now / deploy but restart at <time> / deploy at next scheduled
  restart" (restarts disconnect players).
- Notifications: Discord webhook / email / Slack on deploy success/fail.

### 6.5 Isolation
- Isolation modes chosen per app: **shared** (`hotel` user owns everything) or **isolated**
  (one Linux user per app: `turbo`, `nitro`, `turboweb`, ...).
- Home layout: `/home/<user>/<app>/{releases,shared,current}`, perms `750`; nginx reads
  static outputs via ACL (`setfacl -m u:www-data:rx`) instead of world-readable files.
- Separate DB user per app; systemd sandboxing per unit; per-user resource limits.
- **SFTP-only chrooted users** per app (e.g. uploading assets into the assets host) — no shell.
- Panel role scoping can restrict a panel user to specific apps/stacks.

### 6.6 Server management
- **Dashboard**: CPU, RAM, swap, disk, network, load; per-app usage from systemd cgroups;
  history (SQLite time series, downsampled, configurable retention); online players from
  Turbo if an endpoint exists (see upstream asks).
- **Logs**: journald explorer (per unit, level, time range, search, live tail), nginx logs,
  deploy logs, auth logs, panel audit log. Download/export.
- **Services**: all systemd units (managed + system), start/stop/restart.
- **Packages**: apt upgradable list, security updates, apply with log, reboot-required notice
  + scheduled reboot.
- **Firewall**: see 6.9.
- **SSH**: authorized_keys per system user (add/remove/label, fingerprints), generate
  keypairs, toggle password auth/root login with safety checks.
- **Users**: system users list, which are panel-managed.
- **Files**: scoped file manager per app (browse, edit with Monaco, upload, download,
  permissions) — never outside the app's home unless Owner.
- **Terminal**: web terminal as an app's user; root terminal only for Owner with 2FA re-auth
  (can be disabled at install).
- **Backups**: unified jobs (DBs, app shared dirs, panel state) → local/S3/SFTP; retention;
  restore wizard.
- **Alerts**: disk > X%, RAM pressure, service down/crash loop, cert expiring, deploy failed,
  backup failed, updates pending → Discord / email / webhook.
- **Runtimes**: install/upgrade .NET SDKs and Node versions side-by-side under
  `/opt/turbo-stack/runtimes`; apps pin versions.
- **Panel self-update**: channel, changelog, one-click update with automatic rollback.

### 6.7 Authentication, roles, security
- Local accounts; **TOTP + passkeys (WebAuthn)**; enforced 2FA for admin roles; recovery codes.
- Built-in roles: **Owner**, **Admin**, **Developer** (deploy, logs, env of assigned apps),
  **Operator** (restart, console, logs), **Viewer**. Custom roles from a permission matrix
  (`sites.write`, `deploy.run`, `db.manage`, `terminal.root`, ...), scoped globally / per
  stack / per app.
- Sessions list + revoke; idle timeout; re-auth (sudo mode) for dangerous actions.
- **API tokens** with scopes + expiry (for CI / scripts); OpenAPI docs.
- Audit log of every mutating action (who, what, before/after diff, IP).
- Login rate limiting + fail2ban jail, optional panel IP allowlist, optional "panel only via
  SSH tunnel" mode.
- Secrets encrypted at rest (ASP.NET Data Protection, key file root-only + backed up).
- CSRF, strict CSP on the panel itself, secure cookies.

### 6.8 Cloudflare integration (default edge)

**Guided token creation** (wizard step + Settings → Integrations → Cloudflare):
1. Explain *why* in one sentence (DNS, SSL, cache purge from the panel) and that we use a
   **scoped API Token**, never the Global API Key.
2. Button opens `dash.cloudflare.com/profile/api-tokens` (with permissions pre-filled via the
   dashboard's template link parameters if Cloudflare still supports them; otherwise the
   panel shows a step-by-step checklist with screenshots).
3. Required permissions, shown as a copyable checklist:

   | Scope | Permission | Used for |
   |---|---|---|
   | Zone → Zone | Read | list zones, plan detection |
   | Zone → DNS | Edit | create/update hotel records |
   | Zone → Cache Purge | Purge | purge on deploy / manual purge |
   | Zone → Zone Settings | Edit | SSL mode, Always HTTPS, WebSockets, min TLS, HTTP/3, Brotli |
   | Zone → SSL and Certificates | Edit | Origin CA certificates, Authenticated Origin Pulls |
   | Zone → Cache Rules | Edit *(optional)* | bypass cache for `/api/*`, long cache for `.nitro` |
   | Zone → Firewall Services / WAF | Edit *(optional)* | rate-limit / challenge rules for login + register |

   Zone resources: *Include → Specific zone → myhotel.com* (recommended) or all zones.
   Optional *Client IP filtering* = this server's IP.
4. User pastes the token → panel calls `/user/tokens/verify`, then **probes each permission**
   and shows a green/red checklist ("Cache Purge missing — purge-on-deploy will be disabled").
   Token stored encrypted; expiry date tracked and alerted.

**What the panel manages once connected:**
- **DNS**: records for every site (A/AAAA, proxied toggle per record), drift detection
  ("record changed outside the panel"), one-click fix, import of existing zone records.
- **SSL/TLS**: sets zone mode to **Full (strict)**, issues a **Cloudflare Origin CA cert**
  (15-year, covers `myhotel.com` + `*.myhotel.com`) installed into nginx; optional
  **Authenticated Origin Pulls** so the origin only accepts Cloudflare (the panel host
  included). Let's Encrypt via DNS-01 only for hosts the user sets to DNS-only (grey cloud).
- **Zone settings presets** for a hotel: WebSockets on (required for `ws.`), Always Use HTTPS,
  min TLS 1.2, HTTP/3, Brotli, Rocket Loader **off** (breaks SPAs), Email obfuscation off for
  the client host.
- **Cache**: purge everything, purge per hostname (client/admin/web), purge by URL/prefix;
  **purge-on-deploy** per app (e.g. nitro-client deploy purges `play.myhotel.com`); cache
  rules preset (bypass `/api/*` and `/gamedata/*` dynamic endpoints, cache `.nitro` and
  images long). Falls back to purge-everything if the plan lacks targeted purge.
- **Real visitor IPs**: nginx `set_real_ip_from` list for Cloudflare ranges, refreshed daily
  from `api.cloudflare.com/client/v4/ips`, so Turbo/turbo-web see real client IPs
  (`X-Forwarded-For` chain stays loopback → nginx).
- **Security helpers**: "Under attack" mode toggle, WAF/rate-limit presets for
  `/api/auth/*` and registration, development-mode toggle (bypass cache temporarily).
- **Analytics glance**: requests, cached %, threats per hostname on the dashboard.
- Everything has a non-Cloudflare equivalent or is cleanly hidden when no token is set.

**Consequences of everything (panel included) being proxied — designed in, not bolted on:**
- **Client IP everywhere = `CF-Connecting-IP`**, restored by nginx `real_ip` (trusted only
  from Cloudflare ranges). The panel's audit log, sessions, rate limiting and IP allowlist
  all use the restored IP; requests to proxied hosts that don't come from Cloudflare are
  dropped by the firewall anyway.
- **Bans can't be done in UFW for proxied traffic** (it would ban Cloudflare itself). For
  hostnames behind Cloudflare, fail2ban uses a custom action that adds the IP to a
  **Cloudflare IP list / WAF block rule via the API**, with an nginx `deny` map as a fallback.
  UFW bans stay for direct traffic (SSH, Turbo TCP).
- **Upload limit**: Cloudflare caps request bodies (100 MB on Free/Pro), so panel uploads
  (SQL imports, asset archives, file manager) use **chunked/resumable uploads**; large
  imports can also be pulled from a URL server-side.
- **Long-lived connections**: SignalR (panel) and the game WebSocket send keep-alives well
  under Cloudflare's idle timeout; nginx timeouts are set accordingly.
- **Panel caching**: a cache rule bypasses cache for the panel host entirely.
- **Lost Cloudflare access / broken DNS**: `sudo ts-cli panel expose-ip` reopens the
  IP:8443 entry point with a new one-time token and a timed auto-close.

### 6.9 Firewall management
- Backend: **UFW** with all panel rules tagged (`comment "ts:<id>"`), so manual rules from
  SSH are preserved and shown as "external". Default policy deny inbound / allow outbound.
- **Rules UI**: allow/deny/limit, port or range, TCP/UDP, source (any / IP / CIDR / named
  IP list / "Cloudflare ranges"), comment, enable/disable without deleting, ordering.
- **Presets / services**: SSH, HTTP/HTTPS, panel setup port, MySQL/Postgres remote access,
  Turbo TCP 30000 (Flash clients), custom. Recipes request ports and the panel proposes rules
  (with approval), never silently opens them.
- **Cloudflare-only web mode (default when Cloudflare is on)**: 80/443 allowed only from
  Cloudflare IP ranges (auto-refreshed), blocking direct-to-origin bypass.
- **Exposure audit**: compares *listening sockets* (`ss -tulpn`) with firewall rules and
  flags risks, e.g. "Orleans 11111 listening on 0.0.0.0 — blocked ✓", "MySQL listening on
  0.0.0.0 and allowed from anywhere ✗". Runs after every deploy and on the dashboard.
- **Lockout protection**: changes affecting SSH or the panel are applied with a
  **60-second auto-revert** unless confirmed from a fresh request; the current admin IP is
  never removed without explicit confirmation; recovery command printed
  (`ts-cli firewall reset-safe`) for console access.
- **SSH hardening**: `limit` rule on 22, optional custom SSH port (moves rule + sshd config
  together, with the auto-revert safety), optional "SSH only from IP list".
- **fail2ban**: jails for sshd, panel login, nginx auth/bot patterns, turbo-web login
  (via nginx logs); view bans, unban, ban manually, whitelist, ban duration settings.
  Ban action depends on the path: UFW for direct traffic, Cloudflare API block for proxied
  hostnames (6.8).
- **IP lists**: named allow/block lists (e.g. "Staff IPs") reusable across firewall rules,
  nginx allow/deny (admin host), and the panel's own IP allowlist; optional block of known
  bad IP feeds.
- **Logging**: UFW blocked-connection log viewer with top sources/ports.
- Rule history + diff + revert, like nginx configs.

---

## 7. Turbo/Nitro specifics the recipes must handle

- **Turbo env mapping**: reuse the key mapping from `turbo-cloud/scripts/ploi/lib.sh`
  (flat `TURBO_*` → `TURBO__Turbo__...`) so the panel's env editor shows friendly names.
- **Generate real crypto keys** for `Turbo:Crypto` on install (defaults in appsettings are
  public) and a random `Assets:SecretKey`.
- **Absolute shared paths** for `Turbo:Assets:Directory`, `Turbo:Camera:StoragePath`,
  plugin folder — the Ploi scripts leave these in the release dir (data loss on prune).
- Bind WS to `127.0.0.1:9001`; TCP 30000 off unless "Flash clients" enabled (then firewall
  rule); **always firewall Orleans 11111/3000**.
- `migrate --status` as a deploy gate + migration status badge in UI.
- Read emulator log for the admin setup link after first start and surface it in the wizard.
- turbo-admin/turbo-web: `git submodule update --init`, Node 22+/Yarn 4 build, SPA vhost with
  `/api/` proxy and the CSPs from their `scripts/ploi/nginx.conf`.
- **nitro-react config**: write repo-root `.env` (`VITE_SOCKET_URL`) before build; render
  `nitro-config.json` into shared and serve it at `play.../config/nitro-config.json`; patch
  `dist/index.html`'s `nitro.config.url` post-build (until upstream fix, section 10).
- **nitro-imager**: allocate port, render `IMAGER_*`, preflight every URL/path (it hard-fails
  on missing ones), `MemoryMax` sized from `IMAGER_MAX_LIBRARIES`.
- **Assets host**: static vhost with CORS `*`, long caching for `.nitro`, gamedata proxy
  `/gamedata/` → Turbo 8094.
- Build memory: serialize the Vite build with other heavy builds on low-RAM servers.

---

## 8. Testing with Docker

Docker is the test and dev environment. The thing under test is "a fresh Ubuntu server", so
the core image is a **systemd-booted Ubuntu container** that behaves like a VPS.

### 8.1 The "fresh server" image
- `test-env/server/Dockerfile` with `ARG UBUNTU=24.04|26.04`: stock `ubuntu:<ver>` + `systemd`,
  `systemd-sysv`, `openssh-server`, `sudo`, `curl`, `ca-certificates` — and nothing else, so
  `install.sh` does all the real work. Entrypoint `/sbin/init`.
- Run flags: `--privileged --cgroupns=private --tmpfs /run --tmpfs /run/lock`
  (`/sys/fs/cgroup` writable, needed for systemd + per-unit `MemoryMax`), `NET_ADMIN` for
  UFW/iptables inside the container's own network namespace (never touches the host's rules).
- **Known gaps vs a real VPS**, detected by the panel (`systemd-detect-virt --container`) and
  reported as "skipped in container", never silently passing: swap files, kernel sysctls,
  hostname/timezone via `hostnamectl`/`timedatectl` (partially), reboots, unattended kernel
  upgrades. These are covered by a manual pre-release run on a real VPS (below).

### 8.2 The test network (`test-env/compose.yaml`)
| Service | Purpose |
|---|---|
| `server` | the fresh-server image above; ports 8443/80/443 published for local poking |
| `dns` | CoreDNS: resolves `*.hotel.test` and `panel.hotel.test` → `server`, `api.cloudflare.test` → fake, `github.test` → fake |
| `fake-cloudflare` | small ASP.NET minimal-API fake of the Cloudflare v4 endpoints we use (tokens/verify, zones, DNS records, settings, purge, origin CA, IP lists, `/ips`). Records every call so tests can assert on them; can be told to fail/rate-limit. Origin CA certs signed by a test CA. |
| `fake-github` | OpenSSH git server with bare repos (deploy-key auth per repo) + a fake REST API for keys/hooks/commits. Fires signed `push` webhooks at the panel on demand. |
| `pebble` | Let's Encrypt's test ACME server, for the non-Cloudflare/grey-cloud TLS path |
| `edge` | nginx acting as "Cloudflare proxy": terminates TLS for `*.hotel.test`, adds `CF-Connecting-IP`, forwards from a fixed "Cloudflare range" IP — lets us test real-IP restore, Cloudflare-only firewall and fail2ban-via-API |
| `install-host` | serves locally built `install.sh`, `latest.json`, signed bundles (stands in for the install domain) |
| `browser` | Playwright runner, using `dns`, trusting the test CA |

The panel's external endpoints (Cloudflare API base, GitHub API/SSH host, ACME directory,
install domain) are configuration, never hard-coded — that's what makes the fakes possible.

### 8.3 Test tiers
1. **Unit** (`dotnet test`, no Docker): template renderers (nginx/systemd/env/nitro-config)
   as golden-file tests, wiring-graph resolution + change diffs, recipe manifest validation,
   permission matrix, Cloudflare/GitHub clients against recorded fixtures.
2. **Agent integration** (Testcontainers for .NET, one `server` container per test class):
   every agent operation against real systemd/nginx/UFW/MySQL/MariaDB/Postgres — e.g. write
   a broken vhost → assert `nginx -t` rollback; firewall change without confirm → assert
   auto-revert after the timeout; create isolated users → assert they can't read each other.
3. **Install + wizard E2E** (compose + Playwright): fresh container → `curl install-host |
   bash` → read setup URL from output → Playwright completes the wizard (fake Cloudflare
   token, fake GitHub deploy keys) → asserts:
   - panel moved to `panel.hotel.test` and IP:8443 closed;
   - `play.hotel.test` loads the client and its WebSocket reaches the emulator;
   - `imager.hotel.test/avatarimage?figure=…` returns an image;
   - `admin.` and website load and talk to their `/api/`;
   - fake Cloudflare received the expected DNS records/settings/purges;
   - exposure audit reports no unexpected open ports.
4. **Deploy E2E**: push to `fake-github` → signed webhook → only the affected app redeploys
   → health check; push a broken build → automatic rollback; domain change → only the
   expected rebuild/restart/reload actions happen.
5. **Upgrade tests**: install release N, create a hotel, self-update to N+1, assert nothing
   changed; 24.04 → 26.04 in-place `do-release-upgrade` stays a VPS-only manual check.

**Fixtures:** small fake repos shaped like turbo-cloud/nitro-next (same build commands,
seconds to build) for every-PR runs; the **real** turbo-cloud and nitro-next (pinned commits,
mirrored into `fake-github`) in a nightly full run, since their builds take minutes.

**Speed:** after `install.sh`, the container is `docker commit`-ed to a snapshot image, so
later tests start from "panel installed" or "hotel installed" instead of from scratch.

### 8.4 Developer loop
- `./test-env/dev up` → compose stack with the panel + agent built from source and
  bind-mounted into `server` (restarted on rebuild), Vite dev server on the host proxying
  `/api` and SignalR to the container. Open `https://localhost:8443`.
- `./test-env/dev reset` → back to the "fresh server" or "panel installed" snapshot.
- `./test-env/dev shell` → root shell in the fake server to inspect what the panel did.
- Works on Windows via Docker Desktop (WSL2 backend; cgroup v2 required) and on Linux.

### 8.5 CI (GitHub Actions)
- Every PR: unit + agent integration + install/wizard E2E with fake fixtures, matrix
  Ubuntu 24.04 / 26.04 server images (GitHub's Ubuntu runners allow privileged containers).
- Nightly: full E2E with the real turbo-cloud and nitro-next.
- On failure: upload Playwright traces, panel/agent logs, `journalctl` dump and rendered
  configs from the container as artifacts.
- Before each stable release: one manual run of the install script on a real throwaway VPS
  behind a real Cloudflare test zone (covers the container gaps in 8.1).

---

## 9. Milestones

| # | Milestone | Deliverable |
|---|---|---|
| M0 | Foundation | Repo, **Docker test env (systemd server image, compose, dev loop) + CI**, panel+agent skeleton, gRPC contract, install script, setup-token claim, owner account + 2FA, embedded React shell. |
| M1 | Server core + firewall | Dashboard metrics, systemd services, journald logs, **full firewall management (6.9: rules, IP lists, lockout auto-revert, exposure audit, fail2ban)**, SSH keys, packages, audit log. |
| M2 | Sites + Cloudflare | nginx renderer + test/rollback, site types, config history; **Cloudflare guided token, DNS, Origin CA, zone presets, cache purge, real-IP, Cloudflare-only firewall mode**; ACME fallback. |
| M3 | Databases | Engine abstraction; MySQL (default) / MariaDB / Postgres install, DB/user/grant mgmt, import/export, backups, engine-swap job (MySQL↔MariaDB). |
| M4 | Deployments | Repos (public + private: deploy keys, provider tokens, submodule keys), webhooks, release pipeline, rollback, purge-on-deploy, runtimes manager, systemd app units. |
| M5 | Recipes | Manifest engine (incl. supported DB engines) + turbo-emulator, turbo-admin, turbo-web, nitro-client, nitro-imager, static-assets. Each deployable by hand. |
| M6 | Hotel stack | Wiring graph, stack map UI, full setup wizard, domain-change propagation, `--unattended` config. **= the "minutes to a hotel" goal.** |
| M7 | Hardening & ops | Isolation modes, SFTP chroot, fine-grained roles/scopes, API tokens, alerts, file manager, terminal, emulator console. |
| M8 | Polish | Panel self-update, backup targets, docs site, upgrade tests 24.04→26.04. |

**Status (2026-10-11):** M0 implemented — solution + gRPC contract, root agent with SO_PEERCRED
peer check, panel with setup-token claim / owner + TOTP / recovery codes / login / audit log,
React shell (setup wizard, login, dashboard), `install.sh` + systemd units + `ts-setup-token`,
`scripts/package.sh`, Docker test env + E2E scripts, CI workflow. Unit/API tests pass locally;
the Docker E2E has not been run yet (no Docker on the dev machine) — first run will be in CI or
after installing Docker Desktop. Deviation: the UI ships as `wwwroot/` next to the single-file
panel binary rather than embedded in it.

**Status (2026-10-11):** M1 implemented — agent services for systemd, journal, metrics, ufw,
fail2ban, SSH keys/sshd and apt/reboots (typed, validated, no shell); panel permissions model,
background jobs with SSE output, metrics history (7 days, downsampled), firewall desired state
with planner/diff, risk assessment, own-IP lockout acknowledgement, 60 s confirm-or-revert (also
reverted if the agent restarts), IP lists, exposure audit, fail2ban (incl. a panel sign-in jail),
audit log; UI pages for dashboard charts, services, logs (live tail), firewall, SSH, updates,
activity. 97 unit/API tests pass locally; the Docker E2E now also exercises real ufw (including
an unconfirmed change reverting) but has still not been run. Deferred from M1: custom SSH port
(needs the firewall + sshd change as one revertible transaction), cron/timers UI, files, terminal
(M7). Logs/jobs stream over SSE rather than SignalR.

**Status (2026-10-11):** M2 implemented.
- *Sites:* domains + ordered routes (static, SPA, proxy, WebSocket, redirect) rendered to nginx,
  optional hand-written config, revision history with restore, DNS check. The agent refuses
  configs that escape allowed paths or load code, runs `nginx -t`, and rolls back on failure.
- *TLS:* Let's Encrypt (HTTP-01 via the agent's webroot), self-signed and Cloudflare Origin
  (zone-wide `zone` + `*.zone`); sites go live over HTTP first and switch to HTTPS when the
  certificate job finishes; hourly renewal checks.
- *Cloudflare:* guided token creation, permission probes (harmless writes, undone), DNS sync
  with conflict reporting/replacement, zone presets, cache purge, real-IP config and a system
  "Cloudflare" IP list refreshed daily, "only Cloudflare may reach 80/443" firewall mode.
- *Panel address:* system site for `panel.<domain>`, browser check from the new address,
  one-time sign-in handover, closing the setup address (nginx + firewall, confirmed from the
  new address), `sudo ts-panel-recover` to reopen it.
- *Tests:* 179 unit/API tests (fake agent + fake Cloudflare in-process); the Docker E2E gains a
  fake Cloudflare service and covers the whole flow, still not run (no Docker here).
- *E2E (2026-10-11):* first real runs found 4 Linux-only bugs (nginx 1.24 `http2` syntax,
  reload race, leaked `NOTIFY_SOCKET`, missing-routes crash); after fixes the full Docker
  suite passes on Ubuntu 24.04 and 26.04.
- *M3 status (2026-10-11):* databases implemented. MySQL / MariaDB / PostgreSQL from Ubuntu's
  packages (MySQL and MariaDB mutually exclusive), administered as the local admin over the
  socket (no stored admin passwords; SQL on stdin). Databases, users, access levels
  (PostgreSQL: full or none for now), generated passwords kept encrypted for app wiring,
  backups (gzip, root-only), download, raw upload of .sql/.sql.gz, restore-replaces, final
  backup before drop, daily schedules with retention, SSH-tunnel instructions instead of
  opening ports. E2E installs real MySQL + PostgreSQL and checks logins and isolation on
  24.04 and 26.04. Later: MySQL↔MariaDB migration job, remote backup targets (M8), tuning,
  process list. Ubuntu 24.04 ships MySQL 8.0 (past Oracle's EOL), so the panel recommends
  MariaDB there and MySQL where Ubuntu's MySQL is 8.4+ (26.04); the E2E installs the recommendation.
- *M4 status (2026-10-11):* deployments implemented. Repositories belong to a Linux user (group
  `ts-apps`, home 750, never root); private repos and private submodules each get their own
  deploy key (ssh alias per key, pinned GitHub host keys, submodule URLs rewritten per key).
  Apps: build script with runtimes on PATH (dotnet via dotnet-install, Node from nodejs.org with
  checksum check + corepack), releases under `apps/<app>/releases` with shared paths and an
  atomic `current` swap, static output for sites, optional systemd service (`ts-app-<app>`,
  root-owned launcher, encrypted environment, memory limit) with a health check and automatic
  rollback, one-click rollback, pruning, Cloudflare purge after deploy. GitHub push webhooks
  (HMAC, replay protection, branch + path filters) trigger auto-deploys. The Docker E2E adds a
  fake GitHub (SSH git server enforcing per-repo deploy keys) and covers private repo +
  private submodule, Node build, static site + proxied service, signed webhook, rollback,
  unhealthy release and failed build. The first runs found that runtime archives kept their
  builder's uid 1000 (which could be an app user) and that corepack needs `node` on PATH; the
  agent now extracts without owners and forces `root:root` on runtimes. Not yet: GitHub App / provider-token auth (deploy keys
  only), deploy notifications, zero-downtime (socket-activated) service swaps.
- *M5 status (2026-10-11):* recipes implemented. YAML manifests embedded in the panel
  (Recipes/Manifests), validated at start-up: inputs, database (allowed engines), ports, generated
  secrets, the app (build, static output, service, shared paths, environment) and the site.
  A deliberately small template language (`{{ input.x | origin ?? '' }}`, `{{ x ? 'a' : 'b' }}`);
  environment entries that use an empty input are left out so the app's own default applies.
  Creating from a recipe plans everything first (the UI preview shows it, secrets excepted), then
  creates or reuses the repository (one checkout for turbo-cloud's three apps), the database and
  user, the app and its site. Recipes: turbo-emulator (fresh RSA e=3 keys, shared assets/camera/
  plugins, `migrate` in the build so a failed migration never goes live, WebSocket site),
  turbo-admin (HTTPS required, CSP, `/api/` → admin API), turbo-web (CSP with client and imager
  origins, `/api/` and `/gamedata/`), nitro-client (socket baked in; site-config.json layered after
  the shipped config by rewriting `nitro.config.url`), nitro-imager (IMAGER_* from the assets and
  gamedata addresses, memory limit, `/health`), static-assets (an upload folder owned by the app
  user, default ACL for nginx, CORS + caching), plus generic static-site, node-service and
  dotnet-service. Ports are allocated from the recipe default, skipping other apps' ports and
  anything listening. In M5 the addresses between apps are typed by hand; M6 wires them.
  `test-env/dev recipes` deploys the Turbo/Nitro recipes from the real GitHub repositories
  (`LOCAL_NITRO=<path>` serves a local nitro-next checkout through the fake GitHub instead).
  Real-repo runs: turbo-cloud from GitHub (emulator built, migrated on MariaDB and healthy in
  ~100 s; admin and web built and served) and nitro-next from the local checkout (client built
  with the hotel's config; imager healthy and rendering avatars). GitHub's nitro-next main is
  behind the local branch (old node-canvas/gl imager), so it only deploys once that is pushed.
  Found on the way: .NET needs ICU (installer now adds libicuNN); Docker's /tmp is noexec, which
  broke Yarn's binary wrappers (builds now get a private TMPDIR); an nginx reload race where old
  workers still answered (the agent now waits until they stop accepting); nitro-next's lockfile
  lists the untracked nitro-studio workspace, so its recipes install non-strictly.
- *M6 status (2026-10-12):* hotels implemented. A stack (hotel) comes from a preset
  (Recipes/Presets/turbo-nitro.yaml: role → recipe → default subdomain, plus hotel-wide settings)
  and keeps the answers: domain, host per role, settings, pinned values, sources, database.
  Wiring: recipes export values built only from their site and ports (so the graph can't loop);
  inputs say `from: <role>.<export>` or `settings.<key>`; a left-out role (e.g. assets on another
  host) is replaced by settings. Re-rendering is a three-way merge against what the recipe last
  rendered (kept encrypted with the generated secrets): untouched values follow the hotel,
  values edited by hand stay and are reported, sites edited by hand are left alone. A domain
  change is previewed per member (nginx reload / restart / rebuild) and applied as one job. The
  install job: runtimes → database engine → apps and sites (emulator first, its ports feed the
  others) → certificates one at a time → repository access (stops with the deploy keys to add;
  running it again continues) → deploys in order → the owner's admin setup link (found in the
  emulator's log). UI: the hotel wizard (basics, domains, sources, database, hotel, review), the
  hotel map (role cards with domain, app health, ports, wired values) and the domain editor with
  its preview. Unattended: `install.sh --config hotel.yaml` (the hotel page's "Answers file")
  sets the hotel up at first start; the owner still claims the panel with the setup link.
  `test-env/dev hotel` runs the whole flow from the local turbo-cloud + nitro-next commits.
  Not yet: adding or removing a role from an existing hotel, a graphical stack map with live
  connectivity checks per edge, wizard steps for the panel address and server basics (those
  pages exist separately).
- *M7 status (2026-10-12):* hardening and operations implemented.
  - *Team:* invite links (password + TOTP on first visit), roles (Owner, Admin, Developer, Operator,
    Viewer; terminal.root is the owner's alone), members limited to some hotels/apps (apps, deploys,
    hotels, app services, logs, sites and jobs are filtered; server-wide pages follow the role),
    sessions as rows (listed, revoked; role changes sign people out), "sudo mode" (the second
    factor again, 10 minutes) for team changes, API tokens and the root terminal.
  - *API tokens:* `Authorization: Bearer ts_…`, SHA-256 stored, a subset of the user's
    permissions, optional expiry, last-used; never for team management or root.
  - *Alerts:* every minute — disk, memory, failed or crash-looping services, certificates
    (expiring/failed), security updates, failed jobs (deploys, installs, backups, certificates);
    announced when they start and when they clear, to Discord, webhooks or email (SMTP).
  - *Emulator console:* a service can read commands from a FIFO systemd keeps open as its stdin
    (ts-app-<app>-console.socket); the panel types into it (audited) and shows the journal.
    The Turbo recipe turns it on.
  - *Terminal:* xterm.js over a WebSocket to the agent's pseudo-terminal (openpty + setsid -c),
    as an app user (runuser -l) or root (owner, sudo mode); same-origin check; audited.
  - *Files:* app users' homes, every operation as that user (runuser), ".." refused; edit, upload,
    download, rename, delete; audited.
  - *Isolation:* a hotel can give each part its own Linux user (<hotel>-<role>).
  - *SFTP accounts:* group ts-sftp, sshd Match block (chroot /srv/ts-sftp/<user>, internal-sftp,
    no TTY/forwarding), the folder bind-mounted by a systemd mount unit. The account shares the app
    user's uid/gid (useradd --non-unique), so uploads belong to the app whatever modes the client
    sets (ACL masks broke this; sftp-server has no forced-mode option on 24.04/26.04). Deleting
    one kills only its sshd sessions and uses userdel --force (the uid is busy with the app).
    Password (shown once) and/or keys (root-owned file).
  - All of it is covered by the Docker E2E on the real server (bearer token through nginx, alert
    checks, console via the FIFO, root and app-user shells over websocat, files, sftp/ssh).
  - Not yet: custom roles from a permission matrix, passkeys, OpenAPI docs, Monaco editor,
    per-unit sandboxing beyond NoNewPrivileges/PrivateTmp/ProtectSystem=full.
- *M8 plan (polish), in three parts:*
  1. **Signed releases + panel self-update + upgrade tests.** Channel files
     (`channels/<channel>.json`) are signed with an ECDSA P-256 key (`.sig`, verified with
     `openssl` in install.sh and with .NET's ECDsa in the agent — no extra tooling; replaces
     the minisign idea). The public key is embedded in install.sh and kept in
     `/etc/turbo-stack/release-key.pem`. The panel checks its channel every 6 h; the
     update itself is done by the agent: it fetches and verifies the channel file and bundle
     on its own (it never trusts the panel's copy), then starts the new bundle's `ts-update`
     as a transient systemd unit. `ts-update` keeps the previous binaries and a copy of the
     panel database, swaps, health-checks, and rolls back automatically if the new version
     doesn't come up. Upgrade E2E: install N, build state (apps, site, database, sessions),
     update to N+1 and assert nothing changed; a tampered channel file is refused; a broken
     N+2 rolls back on its own.
  2. **Backup targets** with restic (encrypted, deduplicated; local, S3-compatible and SFTP
     repositories): backup plans of databases, app folders and panel state on a schedule,
     retention (daily/weekly/monthly), snapshots list, restore of database dumps (into the
     database backups list) and folders (next to the original, never over it). MinIO and an
     SFTP server join the Docker test network.
  3. **Docs site** (VitePress, served from the install domain under `/docs`): install,
     wizard, hotels, deploys, backups, updates, security, troubleshooting, and the manual
     24.04 → 26.04 `do-release-upgrade` checklist (that stays a VPS-only check).
- *M8 status (2026-10-12):* polish implemented.
  - *Releases:* `scripts/package.sh` signs every channel file (`scripts/release-key.sh`: the key
    from `RELEASE_SIGNING_KEY`, or a gitignored development key for the test env), embeds the
    public key in install.sh, writes absolute download URLs for GitHub Releases
    (`--download-base`), several channels at once (a stable release also goes to beta) and
    optionally the docs (`--docs`). `.github/workflows/release.yml` builds, signs and publishes
    on a `v*` tag. install.sh refuses an unsigned or wrongly signed channel and keeps the key in
    `/etc/turbo-stack/release-key.pem`.
  - *Self-update:* the agent checks and verifies (UpdateManager), unpacks the bundle in-process
    and starts the new bundle's `ts-update` with `systemd-run` (unit `ts-update`). ts-update keeps
    the previous binaries, units, scripts and a copy of the panel database
    (`/var/lib/turbo-stack-updates/rollback`), waits up to two minutes for the agent socket and
    the panel's `/api/health` to report the new version, and otherwise restores everything.
    `ts-update status|rollback` by hand. The panel checks every six hours, records each
    outcome once in the activity log, and raises alerts (update available; update rolled back).
    `/api/health` reports the installed release (`/opt/turbo-stack/VERSION`).
  - *Agent errors:* a failed system command or a program that can't start now reaches the panel
    as a readable error (program, exit code, its last lines; never its arguments) instead of an
    opaque 502. The command runner can pass environment variables (secrets never in argv).
  - *Backups:* restic from Ubuntu's archive (installed on first use), targets local / S3 / SFTP
    (own ed25519 key per target, host key fingerprint confirmed then pinned), repository
    password generated and shown once (revealable with sudo), plans with databases, app folders
    and panel state (VACUUM INTO + keys + /etc/turbo-stack), daily schedule, retention via
    `forget --group-by tags --prune`, a manifest in each snapshot, restores that never overwrite
    (dumps join the database's backups list; folders go to `~/restored-<time>/`, root-only while
    copying, then chowned without following links; panel state is unpacked for a manual restore).
    The Docker env adds SeaweedFS (S3; MinIO no longer publishes images) and an SFTP server.
  - *Docs:* VitePress site in `docs/site` (install, setup, hotels, deploys, sites, databases,
    backups, files/terminal/SFTP, updates, team, security, alerts, troubleshooting, restoring
    the panel, the Ubuntu 24.04 → 26.04 checklist), built in CI.
  - *Tests:* `test-env/dev upgrade` installs 0.1.0, builds state, updates to 0.1.1 through the
    panel (same session, token, apps, sites), refuses a tampered channel file and rolls back a
    0.1.2 whose panel doesn't start; it runs in CI as its own job. The full E2E gains a backups
    stage (three targets, three plans, a restore) on 24.04 and 26.04.
  - *Not yet:* hosting of the install domain (needs the product name), panel-state restore as a
    one-click action, backup alerts for "no successful run in N days", key rotation for releases.
- *M9 status (2026-10-12):* the items M7/M8 left for later.
  - *Custom roles:* a role is a set from the permission matrix (Permissions.Catalog: area and
    description per permission); members with `Role = Custom` get its permissions as claims on
    every request (cookie validation and token authentication read the role), so edits apply at
    once. The root terminal and team management stay with owners and admins. Team page: roles
    card with the matrix next to the built-in roles.
  - *Passkeys (WebAuthn, fido2-net-lib 4.3):* passwordless sign-in with discoverable,
    user-verified credentials (counts as both factors), "confirm it's you" with a passkey, and
    managing them on the Account page (needs sudo). The relying party is the panel's host name,
    so passkeys need its own domain (never an IP). Ceremony options stay server-side for five
    minutes and are answered once. Tests use a software authenticator (ES256, CBOR) against the
    real verification, including a phished origin and a replay.
  - *API documentation:* the built-in OpenAPI generator (`/api/openapi.json`, signed-in users and
    tokens); every operation says the permission it needs and whether it needs sudo. The API page
    renders it with a filter and request bodies.
  - *Code editor:* Monaco (bundled, never from a CDN, CSP unchanged), syntax highlighting only, a
    side-by-side review of changes before saving, Ctrl/Cmd+S; loaded only when a file opens.
  - *Hotel parts:* client, imager and admin panel joined game assets as optional roles; a part is
    removed (others rewired to the addresses given, then its app and site deleted, uploads kept)
    or added (the install job creates, rewires, certifies and deploys it).
  - *E2E:* the setup flow checks the API document with a token and a custom role; the hotel flow
    removes the admin panel and adds it back.
- *M10 status (2026-10-13):* the leftovers.
  - *Backup staleness alert:* a plan without a successful run (LastSuccessAt, else CreatedAt) for
    `BackupDays` (default 2, Alerts page) raises a warning until it succeeds again.
  - *Release key rotation:* a release signed with the trusted key may carry the next key
    (`release-key.pem` in the bundle, `RELEASE_NEXT_PUBLIC_KEY_FILE` when packaging); ts-update
    trusts it only after that release is installed and healthy (old key kept as
    release-key.previous.pem), install.sh does the same for a verified download, and
    `ts-update trust-key` switches a server by hand. The upgrade E2E rotates: 0.1.3 hands over a
    new key, 0.1.4 is signed with it, 0.1.5 signed with the old key is refused.
  - *One-click panel restore:* owner + sudo; the agent unpacks the snapshot's panel state and starts
    ts-panel-restore in a transient unit, which keeps the current state, swaps in the restored
    database, keys and SFTP key files, and puts the old state back if the panel doesn't come up.
    The outcome is left in panel-restore.json and recorded in the (restored) activity log at
    start-up. The setup-flow E2E restores the panel from its S3 snapshot and keeps going.
- *Deviations / later:* ACME DNS-01 (wildcards without Cloudflare Origin) and Pebble in the
  Docker env are not done yet; fail2ban still bans in UFW even for proxied traffic (banning via
  the Cloudflare API, plan 6.8, comes with the WAF work); Authenticated Origin Pulls not yet.

Each milestone ends with its E2E scenario passing in the Docker test env (section 8). The
fakes grow with the milestones: fake-cloudflare and edge in M2, fake-github in M4.

---

## 10. Recommended upstream changes (small, make deployment much cleaner)

- **nitro-react**: read the config URL from build env (`VITE_NITRO_CONFIG_URL`) or default to a
  relative `./config/nitro-config.json`, instead of hardcoding `nitrodev.co` in `index.html`.
  Ideally move the socket URL to runtime config only, so a domain change doesn't need a rebuild.
- **turbo-cloud**: a loopback health/status endpoint (ready, online users, uptime) for the
  dashboard and deploy health checks (today we can only poll the WS port); optionally
  `sd_notify` READY; option to bind Orleans to loopback.
- **turbo-cloud**: an authenticated loopback command endpoint (or documented stdin console)
  for the panel console and pre-restart "hotel alert" broadcasts.
- **turbo-cloud (planned)**: PostgreSQL support — a provider switch (`Turbo:Database:Provider`)
  plus a separate Npgsql migrations set. The panel then just adds `postgres` to the
  `turbo-emulator` recipe and enables MySQL→Postgres in the engine-swap job.
- **Both**: tag releases so the panel can offer "deploy latest stable" in addition to branches.

---

## 11. Open questions

Resolved: single server ✔ · no nitro-studio ✔ · MySQL default, MariaDB/Postgres swappable
(Turbo Postgres comes later) ✔ · Cloudflare default with guided token ✔ · private repos
supported ✔ · full firewall management ✔ · client on its own subdomain (`play.`), no
path-based layout ✔ · setup via `https://<ip>:8443`, panel then moved behind Cloudflare
(`panel.`) and the IP entry point closed ✔ · install domain + GitHub Releases ✔ ·
GitHub only ✔ · product name Turbo Stack, install domain `install.nitrodev.co` ✔.

