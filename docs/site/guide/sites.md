# Sites and certificates

## Sites

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
