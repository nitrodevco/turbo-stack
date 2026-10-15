# Your first hotel

A hotel is five apps that need to know about each other:

| Role | App | Default address |
|---|---|---|
| Emulator | turbo-cloud's emulator (.NET) | `ws.<domain>` (WebSocket) |
| Client | nitro-react | `play.<domain>` |
| Imager | nitro-imager | `imager.<domain>` |
| Website | turbo-web | `<domain>` |
| Admin | turbo-admin | `admin.<domain>` |
| Assets | an upload folder for game assets | `assets.<domain>` |

## The wizard

**Hotel → New hotel** asks for:

1. **Basics** — the hotel's name and its domain.
2. **Domains** — the address of each role (the defaults are usually right). Leave a role out if it lives elsewhere, e.g. assets on another host; you then type its address once and every app uses it.
3. **Sources** — the GitHub repositories and branches. Private repositories are fine.
4. **Database** — MariaDB or MySQL (the panel recommends the supported one for your Ubuntu release).
5. **Hotel** — settings such as the owner's name and where the game assets live.
6. **Review** — everything that will be created.

Then **Install** runs one job: runtimes → database → apps and sites → certificates → repository access → deploys → the admin setup link.

### Deploy keys

The first install stops at *repository access* for private repositories: each repository gets its own **deploy key** (read-only, for that repository only). The job shows the keys; add each one on GitHub under the repository's *Settings → Deploy keys*, then run **Install** again — it continues where it stopped.

### Isolation

By default all of a hotel's apps run as one Linux user, named after the hotel. On the wizard's first step, tick **A separate Linux user for each part** to give each its own (`<hotel>-emulator`, `<hotel>-client`, …): a compromised website then can't read the emulator's files. Private repositories then need a deploy key per part.

## Wiring

The panel keeps the addresses between apps in step: the client knows the emulator's WebSocket, the website embeds the client, the imager reads the gamedata. Change the domain on the hotel page and the panel shows exactly what will happen — which apps rebuild, which restart, which only reload nginx — before doing it in one job.

Values you edited by hand stay as you set them; the preview tells you which ones those are.

## Adding or removing parts

The emulator and the website are always part of a hotel (the admin panel and website talk to the emulator on the server itself). The **client**, **imager**, **admin panel** and **game assets** are optional:

- **Remove from this server…** on a part's card asks for the address the others should use instead — for example a shared imager or a client hosted elsewhere. The other parts are rewired (restarted or rebuilt as needed), then the part's app and site are removed. Uploaded game assets stay on disk.
- **Add to this server…** on a part that isn't here gives it a domain; the panel creates it, rewires the others to it, issues its certificate and deploys it.

## Rebuilding a hotel from its answers file

The hotel page's **Answers file** downloads everything the wizard was told (no secrets). On a fresh server:

```bash
curl -fsSL https://install.nitrodev.co/install.sh | sudo bash -s -- --config hotel.yaml
```

The hotel is set up as soon as you claim the panel. Bring the data back from [backups](./backups).
