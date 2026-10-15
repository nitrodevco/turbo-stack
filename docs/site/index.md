---
layout: home

hero:
  name: Turbo Stack
  text: A server panel for Turbo and Nitro hotels
  tagline: From a fresh Ubuntu server to a running hotel — emulator, client, imager, website and admin — wired together, on HTTPS, backed up.
  actions:
    - theme: brand
      text: Install
      link: /guide/install
    - theme: alt
      text: Your first hotel
      link: /guide/hotels

features:
  - title: One wizard, one hotel
    details: Pick a domain and your repositories; the panel sets up runtimes, the database, every app, its site and certificate, and wires the addresses between them.
  - title: Deploys from GitHub
    details: Private repositories and submodules with their own deploy keys, push-to-deploy webhooks, health checks with automatic rollback.
  - title: Cloudflare by default
    details: DNS, Origin certificates, real visitor IPs and an origin that only answers Cloudflare — guided, with a scoped token.
  - title: Safe by design
    details: The panel never runs as root; a small root agent does only typed, checked operations. Two-factor sign-in, roles, an activity log of everything.
  - title: Backups that restore
    details: Encrypted, deduplicated snapshots of databases, app folders and the panel itself — on another disk, S3 or SFTP. Restores never overwrite.
  - title: Updates that roll back
    details: Signed releases; one click to update the panel, and if the new version doesn't come up, the previous one is put back by itself.
---
