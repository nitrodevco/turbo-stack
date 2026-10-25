# Updates

## The panel

The **Updates & alerts** page shows the panel's version and checks its release channel every six hours (or when you press **Check now**):

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
sudo ts-update status     # how the last update went
sudo ts-update rollback   # back to the version before the last update
```

### The release key

Every server trusts one release signing key, from its installation (`/etc/turbo-stack/release-key.pem`). If that key ever has to change, the switch happens through a normal update: a release signed with the current key carries the next one, and once that release is installed and healthy the server trusts the next key — and no longer the old one. The update's entry in the activity log says so.

A server that missed that release (or was offline for a long time) can be told by hand, after checking the new key's fingerprint against the one announced with the release:

```bash
sudo ts-update trust-key new-release-key.pem
```

For maintainers: generate the new key, publish one release signed with the current key and with `RELEASE_NEXT_PUBLIC_KEY_FILE` set to the new key's public half (`scripts/release-key.sh`), let servers update, then switch the `RELEASE_SIGNING_KEY` secret to the new key.

## Ubuntu

The same page lists the operating system's pending package updates. Security updates are also installed automatically every day (unattended-upgrades); the page shows when a reboot is needed and can schedule it.

Moving to a new Ubuntu release (24.04 → 26.04) is a bigger step: see [Upgrading Ubuntu](./ubuntu-upgrade).
