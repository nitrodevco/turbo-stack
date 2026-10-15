# Troubleshooting

## I can't reach the panel

- **The panel's domain is broken** (DNS, Cloudflare token, certificate): on the server run

  ```bash
  sudo ts-panel-recover
  ```

  It reopens `https://<server-ip>:8443` (self-signed certificate) and adds a firewall rule for it marked "recovery". Sign in, fix the address under Settings, close the setup address again and delete the "recovery" rule.

- **The panel isn't running**: check both services and their logs.

  ```bash
  systemctl status ts-panel ts-agent
  journalctl -u ts-panel -u ts-agent -n 100
  ```

- **After an update**: `sudo ts-update status` tells how it went; `sudo ts-update rollback` returns to the previous version.

## I lost the setup link

Before the panel is claimed, `sudo ts-setup-token` prints a new link (the old one stops working).

## I lost my phone (two-factor)

Sign in with one of your **recovery codes** instead of a code. Then set up two-factor again on the Account page.

## A deploy fails

The job output on the app page shows the build log. Common causes:

- *Permission denied (publickey)* — the deploy key isn't on GitHub yet, or was added to another repository.
- *A health check didn't pass* — the new release was rolled back automatically; the service's log (Services page) says why it didn't start.
- The build runs out of memory — the Nitro client needs about 2 GB; add memory or swap.

## Where the logs are

| | |
|---|---|
| Panel and agent | `journalctl -u ts-panel -u ts-agent` |
| An app | Services page, or `journalctl -u ts-app-<name>` |
| nginx | `/var/log/nginx/` |
| Sign-in failures | `/var/log/turbo-stack/auth.log` |
