# Apps and deploys

## Repositories

A repository belongs to an **app user** (a Linux user in the `tm-apps` group, never root). Private repositories — and private submodules — each get their own **deploy key**: read-only, valid for one repository. Add it on GitHub under *Settings → Deploy keys*; **Test access** on the repository page confirms it works.

## Apps

An app is a repository plus how to build and run it:

- **Build script** — runs as the app user with the chosen runtimes (.NET, Node) on its `PATH`.
- **Static output** — a folder a site serves (the Nitro client, the website's front end).
- **Service** — a command systemd keeps running (`tm-app-<name>`), with its environment kept encrypted, a memory limit and a health check.
- **Shared paths** — folders that survive releases (uploads, logs, `.env` files).

Each deploy builds a new release under `apps/<app>/releases/` and switches the `current` link atomically. If the service's health check doesn't pass, the previous release is put back automatically. **Roll back to this** next to any kept release on the app page puts it live again in a second.

The emulator's console (if its recipe enables one) lets you type commands into the running process from the app page.

## Push to deploy

Add the webhook shown on the repository page (*Deploy automatically on push*) to the GitHub repository under *Settings → Webhooks*, then tick **Deploy automatically when the branch is pushed** on each app that should follow. Path filters skip pushes that don't touch an app. Deliveries are signed (HMAC) and replays are rejected.

GitHub has to reach the panel for this: move it to its own domain first ([setup](./setup#move-the-panel-to-its-own-address)).

## Recipes

Apps for known projects come from recipes — turbo-emulator, turbo-admin, turbo-web, nitro-client, nitro-imager, static-assets, plus generic static-site, node-service and dotnet-service. A recipe shows everything it will create before it does, including the database, the port it picked and the site. Hotels are recipes wired together.
