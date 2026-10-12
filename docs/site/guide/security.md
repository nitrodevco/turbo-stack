# Security

## How it's built

- The **panel** runs as an unprivileged user in a systemd sandbox. Everything that needs root goes through the **agent**, which only listens on a local socket, only accepts the panel's user, and only performs typed, checked operations — no shell commands from the panel.
- Each app runs as its **own Linux user** (never root), in the `tm-apps` group; homes are private (750).
- Secrets the panel keeps — database passwords, environment variables, tokens, backup passwords — are encrypted at rest.
- Releases of the panel are signed; see [Updates](./updates).

## Signing in

- Two-factor sign-in is required for everyone; recovery codes are given once.
- Failed sign-ins are rate-limited and feed a fail2ban jail.
- Sensitive actions (API tokens, the root terminal, restores, removing targets, updating the panel) ask for the second factor again.
- Every change is in the **Activity** log: who, what, when and from where.

## The firewall

The **Firewall** page manages ufw: rules, IP lists, and an exposure audit of what actually listens. Changes that could lock you out are applied with a **60-second confirmation**: if you don't confirm from the panel (because you can't reach it any more), they're undone on their own.

With Cloudflare in front, **only Cloudflare may reach 80/443** closes the origin to everyone else, so attackers can't bypass Cloudflare by finding your server's IP.

## SSH

The **SSH access** page manages authorized keys per user and the server's SSH settings. It won't turn off password login until an admin user has a key — add yours, check that it works, then require keys.
