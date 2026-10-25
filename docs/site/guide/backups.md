# Backups

Backups use [restic](https://restic.net): snapshots are **encrypted** before they leave the server and **deduplicated**, so a nightly backup only stores what changed.

## Targets

A target is a restic repository:

| Kind | Good for |
|---|---|
| **Local folder** | a second disk mounted under `/mnt` (a folder on the same disk only protects against mistakes, not against losing the server) |
| **S3-compatible** | AWS S3, Backblaze B2, Wasabi, Cloudflare R2, … — endpoint, bucket, access key |
| **SFTP** | a storage box or another server |

When you add a target the panel generates its **repository password** and shows it **once**. Keep it outside this server (a password manager): without it nobody — including you, on a new server — can read the snapshots. **Show password** on the target reveals it again after you confirm your second factor.

To connect a repository that already exists (for example from your old server), enter its password when adding the target.

### SFTP targets

1. Add the target (server, port, user, folder). The panel creates a key for it.
2. Put the shown public key in `~/.ssh/authorized_keys` of that user on the server.
3. **Get the server's fingerprint** and compare it with what your provider shows. Only when it matches, **connect** — the host key is pinned from then on, so a server pretending to be yours is refused.

## Plans

A plan says what to back up, to which target, when, and how long to keep it:

- **Databases** — dumped fresh at the start of each run.
- **Folders** — inside an app user's home, e.g. `hotel:sites/assets` (game assets) or the emulator's shared folders.
- **The panel's own state** — its database, keys and settings, needed to [restore the panel](./restore-panel).
- **Daily at** — the server's local time.
- **Keep** — daily, weekly and monthly snapshots (for example 7 / 4 / 6).

**Run now** starts a plan immediately; its output streams in the page. Failed runs raise an [alert](./alerts), and so does a plan that hasn't saved a snapshot for two days (adjustable under *Updates & alerts → Alerts*) — whatever the reason.

## Restoring

Pick a snapshot, then what to bring back. Nothing is overwritten:

- a **database** dump joins that database's backups list on the Databases page — restore it from there when you're ready;
- a **folder** comes back next to the original, as `~/restored-<time>/<folder>` of the app user, so you can compare and copy what you need;
- the **panel's state** is unpacked under `/var/lib/turbo-stack-backups/restores/` — or, as the owner, [put back in one step](./restore-panel).

Restoring needs your second factor again.
