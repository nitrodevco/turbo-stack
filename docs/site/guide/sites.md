# Sites and certificates

## Sites

Sites and apps share one page, *Apps & sites*. An app lists the sites that serve it, and a site lists the apps it serves. The panel links a site to an app when the site carries the app's name, serves files from the app's folder, or proxies to one of the app's ports. Not every app needs a site (the emulator has none), and a site doesn't need an app: it can serve any folder, proxy to any port or redirect. On an app's page, *Add a site* fills in a site for that app.

A site is one or more domains and an ordered list of routes:

| Route | Does |
|---|---|
| Static | serves a folder |
| SPA | serves a folder, falling back to `index.html` |
| Proxy | forwards to a local port (an app's service) |
| WebSocket | forwards upgrades (the emulator) |
| Redirect | sends visitors elsewhere |

The panel writes the nginx configuration, checks it with `nginx -t` before reloading, and rolls back if nginx refuses it. Every change is kept as a revision you can restore. For special cases a site can carry hand-written configuration; the agent still refuses anything that reaches outside the allowed folders or loads code.

## Certificates

| Kind | When |
|---|---|
| **Cloudflare Origin** | default with Cloudflare: one certificate for `<domain>` and `*.<domain>`, valid 15 years |
| **Let's Encrypt** | without Cloudflare's proxy; renewed automatically |
| **Self-signed** | testing only |

A new site goes live over HTTP first and switches to HTTPS as soon as its certificate is ready. Certificates are checked every hour; the Certificates page shows what expires when, and [alerts](./alerts) warn before it happens.

## Cloudflare

The Cloudflare page shows the zone's DNS records next to what the sites need, reports conflicts, and applies the recommended zone settings. **Purge cache** clears Cloudflare's copy after a change; deploys of static apps purge their own site automatically.
