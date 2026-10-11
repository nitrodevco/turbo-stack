# Turbo Manager

Self-hosted Ubuntu server panel for running a [Turbo](../turbo-workspace/turbo-cloud) + Nitro hotel.
"Turbo Manager" is a working name; see [docs/PLAN.md](docs/PLAN.md) for the full plan.

## Layout

| Path | What |
|---|---|
| `src/TurboManager.Panel` | Web UI host + API (`tm-panel`). Runs unprivileged as `turbo-manager` on `127.0.0.1:7800`. |
| `src/TurboManager.Agent` | Root agent (`tm-agent`). gRPC on `/run/turbo-manager/agent.sock`; only root and the panel user may connect (SO_PEERCRED). |
| `src/TurboManager.Contracts` | gRPC contract between panel and agent (`Protos/agent.proto`). |
| `src/TurboManager.Core` | Shared constants (`Branding`, `ManagerPaths`) and helpers. |
| `web/` | React + Vite + Tailwind UI, served from the panel's `wwwroot` in releases. |
| `install/` | `install.sh`, systemd units and the `tm-setup-token` helper. |
| `scripts/package.sh` | Builds release bundles in `dist/`, laid out like the install domain. |
| `test-env/` | Docker test environment (systemd Ubuntu "fresh server", stand-in install domain, E2E scripts). |

## Develop locally

Requirements: .NET SDK 10, Node 24 with Corepack.

```bash
dotnet test                                    # unit + API tests

# Terminal 1 and 2: agent + panel in Development mode (state in .dev-data/)
cd src/TurboManager.Agent && DOTNET_ENVIRONMENT=Development dotnet run
cd src/TurboManager.Panel && DOTNET_ENVIRONMENT=Development dotnet run
#   the panel logs a setup URL: "Development setup URL: /setup?token=…"

# Terminal 3: UI with hot reload, proxies /api to the panel
cd web && corepack yarn install && corepack yarn dev
#   open http://localhost:5180/setup?token=…
```

On Windows the agent can't read `/proc` or check peer credentials, so it runs with `AllowUnverifiedPeers` and
reports mostly empty system info. Use the Docker environment for anything Linux-specific.

## Docker test environment

Requires Docker with cgroup v2 (Docker Desktop's WSL2 backend works on Windows; use Git Bash or WSL to run the script).

```bash
test-env/dev all        # package → fresh server → curl | bash install → E2E checks (what CI runs)

test-env/dev package    # build dist/ (ARCH=arm64 for ARM hosts)
test-env/dev up         # fresh Ubuntu server (UBUNTU=26.04 to switch)
test-env/dev install    # run install.sh on it, exactly like a user
test-env/dev e2e        # server checks + setup/login flow through nginx on :8443
test-env/dev shell      # root shell on the server
test-env/dev reset      # back to a fresh box
```

After `install`, the setup wizard is at <https://localhost:8443> (self-signed certificate).
