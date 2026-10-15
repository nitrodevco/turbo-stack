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

## Custom roles

When none of the built-in roles fits, **Team → Roles → New role** builds one from the permission matrix: every permission is listed with what it allows, next to which built-in roles have it, and you tick exactly the ones the role should have. Give the role to members like any other.

Edits to a role apply on its members' next click — nobody has to sign in again. Two permissions stay with the owner and admins and can't be part of a custom role: the root terminal and managing the team. A role can't be deleted while members still have it.

## Your account

**Account** lists your signed-in sessions (browser, address, last seen) — sign any of them out. Changing a password or 2FA, creating API tokens and other sensitive actions ask for your second factor again; it then stays confirmed for ten minutes.

## API tokens

For scripts and CI, create an **API token** on the Account page: choose its permissions (never more than your own) and an expiry. The token is shown once. Use it as a bearer token:

```bash
curl -H "Authorization: Bearer ts_…" https://panel.example.com/api/apps
```

A token can only do what it was given, and stops working when it expires, is deleted, or its owner loses the permission.

## The API reference

The **API** page lists every endpoint the panel has — method, path, parameters, request body — with the permission each one needs and whether it asks for the second factor again (those are for signed-in sessions only, never tokens). The same description is available as an OpenAPI document at `/api/openapi.json` for signed-in users and tokens, ready for code generators or tools like Postman.
