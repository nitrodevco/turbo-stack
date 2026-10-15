# Apps and deploys

## Repositories

A repository belongs to an **app user** (a Linux user in the `ts-apps` group, never root). Private repositories — and private submodules — each get their own **deploy key**: read-only, valid for one repository. Add it on GitHub under *Settings → Deploy keys*; **Test access** on the repository page confirms it works.

### Connecting GitHub

Connect a GitHub account under *Apps → Repositories → Connect GitHub* so you don't have to copy keys into GitHub yourself. The panel uses a personal access token: either a classic token with the `repo` scope (the link on that page fills it in), or a fine-grained token with *Administration* (read and write), *Webhooks* (read and write) and *Contents* (read) on the repositories you deploy. The token is stored encrypted and is never shown again.

Once connected:

- **Add repository** lists your repositories and their branches, so you pick one instead of typing it.
- The panel adds the deploy keys (read-only) itself, for private submodules as well, and can create the push webhook. Recipes add their deploy keys the same way.
- Repositories you added before connecting get a one-click **Add on GitHub** button on their page.
- **New secret** also updates the webhook the panel created. Removing a repository removes the keys and webhook the panel added.

The account needs admin rights on a repository to add its deploy key and webhook. If a step fails, the repository page shows why, and you can still add that key or webhook by hand. Disconnecting GitHub leaves the existing keys and webhooks in place, so deploys keep working.

## Apps

An app is a repository plus how to build and run it:

- **Build script** — runs as the app user with the chosen runtimes (.NET, Node) on its `PATH`.
- **Static output** — a folder a site serves (the Nitro client, the website's front end).
- **Service** — a command systemd keeps running (`ts-app-<name>`), with its environment kept encrypted, a memory limit and a health check.
- **Shared paths** — folders that survive releases (uploads, logs, `.env` files).

Each deploy builds a new release under `apps/<app>/releases/` and switches the `current` link atomically. If the service's health check doesn't pass, the previous release is put back automatically. **Roll back to this** next to any kept release on the app page puts it live again in a second.

The emulator's console (if its recipe enables one) lets you type commands into the running process from the app page.

## Push to deploy

Add the webhook shown on the repository page (*Deploy automatically on push*) to the GitHub repository under *Settings → Webhooks*, then tick **Deploy automatically when the branch is pushed** on each app that should follow. Path filters skip pushes that don't touch an app. Deliveries are signed (HMAC) and replays are rejected.

GitHub has to reach the panel for this: move it to its own domain first ([setup](./setup#move-the-panel-to-its-own-address)).

## Recipes

Apps for known projects come from recipes — turbo-emulator, turbo-admin, turbo-web, nitro-client, nitro-imager, static-assets, plus generic static-site, node-service and dotnet-service. A recipe shows everything it will create before it does, including the database, the port it picked and the site. Hotels are recipes wired together.
