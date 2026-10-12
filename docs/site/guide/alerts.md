# Alerts

The panel checks the server every few minutes and tells you when something needs attention:

- disk or memory almost full (thresholds on the Alerts page);
- an app's service failed or keeps crashing;
- a certificate expires soon or couldn't be renewed;
- security updates are waiting;
- a deploy, hotel install, backup, certificate or runtime job failed;
- a panel update is available, or an update didn't work and was rolled back.

Alerts go to **Discord**, a **webhook** (JSON) or **email** (your SMTP server). A channel can receive only critical alerts. Each problem is sent once when it starts and once when it's resolved — not every few minutes. **Test** next to a channel sends a test message.
