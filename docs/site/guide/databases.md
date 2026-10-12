# Databases

The panel installs **MySQL, MariaDB** (one or the other) and **PostgreSQL** from Ubuntu's packages and administers them over the local socket — it stores no database admin password.

On Ubuntu 24.04 it recommends **MariaDB**, because 24.04's MySQL is 8.0, past its end of support; on 26.04 it recommends MySQL 8.4. Turbo works with either.

## Databases and users

Create a database with its own user in one step; the generated password is shown once and kept encrypted for the apps that need it. Users get full or read-only access per database. Database ports stay closed to the internet — the page shows how to reach a database through an SSH tunnel instead.

## Backups on the server

- **Back up** writes a compressed dump under `/var/lib/turbo-manager-backups` (root-only).
- A **schedule** takes a daily dump and keeps the newest *n*.
- **Upload .sql / .sql.gz** to import a dump.
- **Restore…** replaces the database's contents with a dump — take a backup first if you might want the current data back.
- Dropping a database always takes a final backup.

These dumps live on the same server. To keep copies elsewhere, use [backup plans](./backups).
