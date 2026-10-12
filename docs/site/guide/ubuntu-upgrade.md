# Upgrading Ubuntu (24.04 → 26.04)

Turbo Manager supports both releases, but moving a running server from one to the other with `do-release-upgrade` changes many packages at once (nginx, the database servers, OpenSSH, systemd). It can't be tested automatically the way the panel's own updates are, so do it deliberately.

::: tip Often simpler
Install a fresh 26.04 server, set the hotel up there from its [answers file](./hotels#rebuilding-a-hotel-from-its-answers-file), restore the databases and folders from your [backups](./backups), and switch DNS. The old server stays as it was until you're happy.
:::

## Before

- [ ] Update the panel to the latest release (**Updates**).
- [ ] Run every backup plan once more (**Backups → Run now**) and check the snapshots exist on a target **outside** the server.
- [ ] Note the repository passwords of your backup targets (outside the server).
- [ ] Take a provider snapshot of the server if your host offers one.
- [ ] Announce a maintenance window: the hotel is offline during the upgrade.

## During

```bash
sudo apt update && sudo apt full-upgrade
sudo do-release-upgrade
```

When asked about changed configuration files, **keep the installed version** for nginx and sshd (the panel manages them with its own files under `sites-available/` and `sshd_config.d/`).

## After

- [ ] `systemctl status tm-agent tm-panel` — both running.
- [ ] Sign in; the dashboard shows Ubuntu 26.04.
- [ ] **Databases**: the engine is running. MySQL 8.0 → 8.4 and MariaDB upgrades convert data on first start; check the service log if it didn't come up.
- [ ] **Services**: every `tm-app-*` is active; if the emulator isn't, redeploy it (runtimes are the panel's own and survive the upgrade).
- [ ] **Firewall** and **SSH access** pages show what you expect.
- [ ] Open the hotel, play, check the imager and the website.
- [ ] Run a backup plan.
