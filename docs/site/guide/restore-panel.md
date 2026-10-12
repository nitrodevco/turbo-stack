# Restoring the panel

A backup plan with **the panel's own state** keeps the panel's database (accounts, settings, hotels, apps, sites, the activity log), its encryption keys and `/etc/turbo-manager`. This page is about putting those back.

::: warning What this does and doesn't bring back
The panel's state is its *knowledge* of the server — not the apps themselves. On a new server, restoring it gives you your accounts, settings and the description of every hotel; you then redeploy the apps and restore their data (databases, folders) from the same backups. To rebuild a hotel on a fresh server, the [answers file](./hotels#rebuilding-a-hotel-from-its-answers-file) together with your database and folder backups is usually quicker.
:::

## On the same server (the panel's database was lost or damaged)

1. If the panel doesn't start, install it again over the existing installation (`install.sh`); claim it with a temporary owner if it asks to be set up.
2. Under **Backups**, add the backup target again — **with its repository password** — so the existing repository is connected, not a new one created.
3. Pick the latest snapshot with the panel's state, **Restore…**, tick *The panel's state*. It is unpacked under `/var/lib/turbo-manager-backups/restores/panel-state-<time>/`.
4. On the server:

   ```bash
   R=/var/lib/turbo-manager-backups/restores/panel-state-<time>
   sudo systemctl stop tm-panel
   sudo cp "$R/var/lib/turbo-manager/backup/panel.db" /var/lib/turbo-manager/panel.db
   sudo rm -f /var/lib/turbo-manager/panel.db-wal /var/lib/turbo-manager/panel.db-shm
   sudo cp -r "$R/var/lib/turbo-manager/keys/." /var/lib/turbo-manager/keys/
   sudo chown -R turbo-manager:turbo-manager /var/lib/turbo-manager
   sudo systemctl start tm-panel
   ```

5. Sign in with your **old** owner account.

The keys matter: without them the panel can't decrypt the secrets in its database (database passwords, environment variables, backup passwords).

## Files from /etc/turbo-manager

The snapshot also holds `/etc/turbo-manager` (under `$R/etc/turbo-manager`). Copy back only what you need — for example `backup-keys/` for SFTP backup targets or `sftp-keys/` for SFTP accounts. Keep the new server's `install.env` and `release-key.pem`.
