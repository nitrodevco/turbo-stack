# Installing

## What you need

- A server with **Ubuntu 24.04 or 26.04 LTS** (x64 or arm64), fresh — nothing else on ports 80, 443 and 8443.
- **2 GB of memory** at least; 4 GB is comfortable. Below 4 GB the installer adds a 2 GB swap file, because building the Nitro client needs it.
- **20 GB of disk** or more.
- Root access (SSH in as root, or a user with `sudo`).
- A domain whose DNS you can change — ideally on Cloudflare (free plan is fine).

## The one-liner

```bash
curl -fsSL https://install.nitrodev.co/install.sh | sudo bash
```

The installer:

1. checks the system, updates it and installs the base packages (nginx, ufw, fail2ban, …);
2. creates the `turbo-stack` system user and its folders;
3. downloads the panel and agent, **checks the release signature** and the download's checksum;
4. opens the firewall for SSH, HTTP, HTTPS and the setup port (8443), nothing else;
5. starts the panel and prints a one-time setup link:

```
✔ Turbo Stack 0.1.0 installed.
Open: https://203.0.113.10:8443/setup?token=7Gk2…   (expires in 24h)
```

Open it in your browser. The certificate warning is expected: the setup address uses a self-signed certificate until the panel moves to its own domain. The token means nobody who finds your server's IP first can claim the panel.

Lost the link? Run `sudo ts-setup-token` for a new one.

## Options

| Option | |
|---|---|
| `--channel stable\|beta` | Release channel (default: stable). Can be changed later on the Updates page. |
| `--public-host <host>` | The address printed in the setup link (detected automatically). |
| `--config hotel.yaml` | Also set up a hotel from a saved answers file — see [Rebuilding a hotel](./hotels#rebuilding-a-hotel-from-its-answers-file). |
| `--no-root-terminal` | Permanently disable the root terminal in the panel. |
| `--bundle <file.tar.gz>` | Install from a downloaded release bundle (no network needed for the panel itself). |
| `--no-upgrade` | Skip upgrading the system's packages. |

## What goes where

| | |
|---|---|
| `/opt/turbo-stack` | the panel and agent (root-owned) |
| `/var/lib/turbo-stack` | the panel's database and keys |
| `/etc/turbo-stack` | install settings, release key, certificates |
| `/home/<app user>` | each app user's repositories, releases, sites and uploads |
| `/var/lib/turbo-stack-backups` | database dumps, backup staging (root-only) |

Two services run: `ts-agent` (root, no network port — it only listens on a local socket) and `ts-panel` (unprivileged, behind nginx).
