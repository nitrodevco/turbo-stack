# Files, terminal and SFTP

## File manager

**Files** browses an app user's home: create folders, edit text files, upload and download, rename and delete. Everything happens **as that app user**, never as root, so files keep the right owner and nothing outside the home folder is reachable.

Text files open in a code editor with syntax highlighting for the usual languages (C#, TypeScript, JSON, YAML, `.env` and ini files, shell, …). **Review changes** shows your edits side by side with the file as it is on disk before you save; `Ctrl+S` (or `⌘S`) saves.

## Terminal

**Terminal** opens a shell in the browser as an app user — a real terminal, so editors and `Ctrl+C` work. The owner can also open a **root** shell after confirming their second factor (unless the server was installed with `--no-root-terminal`).

## SFTP accounts

For uploading game assets with FileZilla or WinSCP, create an **SFTP account** on the SSH page: it can log in with a password or a key, sees only one folder (for example the assets site's folder), and can't run commands or open tunnels. Uploads belong to the app — the account acts as the app's user inside that folder — so the site serves them right away.

Files uploaded with "only me" permissions (mode 600) can't be read by the web server; most clients upload as readable by everyone, which is what you want for assets.
