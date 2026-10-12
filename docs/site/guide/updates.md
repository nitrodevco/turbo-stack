# Updates

## The panel

The **Updates** page shows the panel's version and checks its release channel every six hours (or when you press **Check now**):

- **Stable** — tested releases.
- **Beta** — earlier access; stable releases reach beta too.

Every release is **signed**. The panel only accepts a release whose channel file carries a valid signature from the key that came with your installation, and whose download matches the signed checksum. A tampered or corrupted release is refused before anything changes.

**Update to …** (after confirming your second factor):

1. the agent downloads and verifies the release;
2. the panel stops briefly — **your apps keep running**;
3. the previous version and a copy of the panel's database are kept;
4. the new version starts and has two minutes to answer;
5. if it doesn't, the previous version **and its database** are put back automatically, and a critical alert tells you why.

The update and its outcome are in the activity log.

### By hand

```bash
sudo tm-update status     # how the last update went
sudo tm-update rollback   # back to the version before the last update
```

## Ubuntu

The same page lists the operating system's pending package updates. Security updates are also installed automatically every day (unattended-upgrades); the page shows when a reboot is needed and can schedule it.

Moving to a new Ubuntu release (24.04 → 26.04) is a bigger step: see [Upgrading Ubuntu](./ubuntu-upgrade).
