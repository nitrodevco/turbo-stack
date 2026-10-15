# install.nitrodev.co

The install domain is a Cloudflare Pages project (`turbo-stack-install`) deployed by the release workflow
(`.github/workflows/release.yml`). It serves:

| Path | From |
|---|---|
| `/install.sh` | the latest stable release (release public key embedded) |
| `/channels/{stable,beta}.json` (+ `.sig`) | signed channel files; a beta release only moves `beta` |
| `/docs/` | the documentation site of the latest stable release |
| `/` | redirects to `/docs/` (`_redirects`) |

The bundles themselves stay on GitHub Releases: the channel files point at them with absolute URLs, so the
repository must be public.

## One-time setup

1. **Signing key.** `openssl ecparam -name prime256v1 -genkey -noout -out release-key.pem`, store it offline, and add
   its contents as the `RELEASE_SIGNING_KEY` repository secret. Every server ever installed trusts this key, so losing
   it means rotating (docs/site/guide/updates.md).
2. **Pages project.** In Cloudflare: *Workers & Pages → Create → Pages → Upload assets*, name it
   `turbo-stack-install`, production branch `main` (or `npx wrangler pages project create turbo-stack-install
   --production-branch main`).
3. **Domain.** In the project: *Custom domains → Set up a custom domain → `install.nitrodev.co`*. With `nitrodev.co`
   on Cloudflare this adds the `CNAME install → turbo-stack-install.pages.dev` record and the certificate itself;
   otherwise add that CNAME at the DNS host.
4. **API token.** *My Profile → API Tokens → Create token → Custom*: permission *Account · Cloudflare Pages · Edit*,
   limited to the account. Add it as the `CLOUDFLARE_API_TOKEN` secret and the account ID (dashboard sidebar) as
   `CLOUDFLARE_ACCOUNT_ID`.
5. **Release.** `git tag -a v0.1.0 -m "First release" && git push origin v0.1.0`. The workflow tests, packages,
   signs, creates the GitHub release and deploys the site.

## Checking it

```bash
curl -fsSI https://install.nitrodev.co/install.sh          # 200, text/plain
curl -fsS  https://install.nitrodev.co/channels/stable.json  # version + bundle URLs on github.com
curl -fsSL https://install.nitrodev.co/install.sh | head -3
```

Then install on a fresh Ubuntu 24.04 VPS with the one-liner.
