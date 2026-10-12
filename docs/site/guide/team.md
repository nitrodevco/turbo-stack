# Team, roles and API tokens

## Members

**Team → Invite someone** creates an invitation link for a new member. They choose a password and set up two-factor sign-in when they accept; until then the invitation is pending.

| Role | Can |
|---|---|
| **Owner** | everything, including the root terminal |
| **Admin** | everything except the root terminal |
| **Operator** | see the server, apps, sites, databases and backups; restart services, deploy, use app consoles — no firewall, SSH or team changes |
| **Developer** | deploy, read logs, use app consoles, files and the app terminal |
| **Viewer** | dashboard and services, read-only |

A member can also be **limited** to some hotels or apps: they then only see and touch those — their apps, sites, services, logs and jobs.

## Your account

**Account** lists your signed-in sessions (browser, address, last seen) — sign any of them out. Changing a password or 2FA, creating API tokens and other sensitive actions ask for your second factor again; it then stays confirmed for ten minutes.

## API tokens

For scripts and CI, create an **API token** on the Account page: choose its permissions (never more than your own) and an expiry. The token is shown once. Use it as a bearer token:

```bash
curl -H "Authorization: Bearer tm_…" https://panel.example.com/api/apps
```

A token can only do what it was given, and stops working when it expires, is deleted, or its owner loses the permission.
