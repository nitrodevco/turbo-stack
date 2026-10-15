# First-run setup

## Claim the panel

The setup link creates the **owner** account: a username, a long password, and two-factor sign-in with an authenticator app (scan the QR code, type a code to confirm). You get **recovery codes** — store them with the password; they are the way back in if you lose the phone.

## Connect Cloudflare

Cloudflare is the default edge: it keeps your server's address private, absorbs attacks and serves certificates that never expire on you.

The panel asks for a **scoped API token**, never your Global API Key. The Cloudflare page walks you through creating it with exactly the permissions it needs (DNS, SSL, cache purge for your zone). The panel tests each permission with a harmless change it undoes immediately.

Once connected, the panel can:

- create the DNS records for each site;
- issue **Origin certificates** (valid for 15 years, trusted by Cloudflare);
- restore visitors' real IP addresses in logs and rate limits;
- let only Cloudflare reach ports 80 and 443 (firewall mode on the Firewall page).

Not using Cloudflare? Sites can use Let's Encrypt instead; everything else works the same.

## Move the panel to its own address

Until now you've used `https://<ip>:8443`. Under **Settings → Panel address** enter `panel.<your-domain>`: the panel creates the site and certificate, checks from your browser that the new address works and signs you in there. Then **close the setup address** with the button on the same card — from then on port 8443 is shut (nginx and firewall).

If the new address ever breaks (a DNS mistake, an expired token), `sudo ts-panel-recover` on the server reopens `https://<ip>:8443` — see [Troubleshooting](./troubleshooting).
