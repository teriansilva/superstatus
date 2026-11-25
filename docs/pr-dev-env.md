# Per-PR ephemeral dev environments + visual snapshots (#274)

Every UI-touching PR can spawn a **throwaway full-stack SuperStatus environment**
on the dev Docker host and get **screenshots of the real running operator console**
— every tab, incl. Alerts — posted back for review. This catches cross-component
render regressions that the markup-only unit tests and the `/ui-gallery` component
harness miss (e.g. the Alerts-tab grid collapse in #273).

## How it works

```
validate.yml (PR push, tests pass)
   └─ UI files changed? ─ dispatch ▶ pr-dev-env-up.yml
                                        ├─ ssh dev host, checkout PR head
                                        ├─ docker compose -f docker-compose.yml
                                        │       -f docker-compose.pr-dev.yml
                                        │       --env-file .env.pr-dev -p ${PR}-dev up -d --build
                                        ├─ wait /health
                                        ├─ Playwright (mcr image) on the PR network:
                                        │     POST /dev/login (capture token) → screenshot
                                        │     public page + each console tab × {desktop,mobile}
                                        └─ upload artifact + comment live URL on the PR
PR closed / daily cron ▶ pr-dev-env-down.yml  (compose down -v + rm workspace; sweeps closed PRs)
```

- **Per-PR isolation**: compose project `${PR}-dev` (own containers/volumes/network),
  workspace `/home/marcusbraun/superstatus-dev/${PR}`, host ports `web = 30000+PR`, `identity = 31000+PR`.
- **Auth for screenshots**: `SuperStatus.Web` exposes `POST /dev/login` **only** when
  `PR_DEV_LOGIN_ENABLED=true` + a non-empty `PR_DEV_LOGIN_TOKEN` are set (the per-PR env
  is the only place `docker-compose.pr-dev.yml` sets them). It signs in a synthetic
  operator cookie so Playwright can reach `/admin` without the OIDC round-trip. The
  staging/production env-file secrets never set these, so the route does not exist there.
- **Capture**: `web/visual/capture-live.mjs` (full `playwright`) drives the live app —
  distinct from the demo-mode `capture.mjs` (`playwright-core`, `/ui-gallery`).

## Required secrets / vars (one-time provisioning)

Set on the `superstatus.io/superstatus` repo (or org):

| Kind | Name | Purpose |
|---|---|---|
| var | `DEV_DOCKER_HOST` | dev Docker host (e.g. `192.168.15.9` / `ro-docker-host-2`) |
| secret | `DEV_DOCKER_HOST_USER` | SSH user the runner connects as |
| secret | `DEV_DOCKER_HOST_SSH_KEY` | private key for that user |
| secret | `DEV_DOCKER_HOST_SSH_KNOWN_HOSTS` | `known_hosts` line for the host |
| secret | `PR_COMMENT_TOKEN` | Forgejo PAT with `issue:write` + `write:repository` (comment + workflow-dispatch) |

Host prep (`ro-docker-host-2`): Docker + `docker compose` + `git` present; the SSH
user owns `/home/marcusbraun/superstatus-dev`; the runner's public key in its `authorized_keys`.

## Verifying / using an env

- **Browse it**: the spawn comment posts `http://<dev-host>:<30000+PR>` (console at `/admin`).
- **Screenshots**: download the `pr-${PR}-screenshots` artifact from the workflow run
  (public page + each console tab, desktop + mobile, + `manifest.json`).
- **Re-spawn**: re-run `pr-dev-env-up` from the Actions UI with the PR number.
- **Tear down now**: run `pr-dev-env-down` with the PR number (else it auto-tears on close).
- **Debug on the host**: `docker compose -p ${PR}-dev ps` / `… logs web api identity postgres`.

## Reviewing a UI change (for Hermes + humans)

1. Does the claimed visual change actually appear in the screenshots?
2. Any clipped/overlapping/placeholder UI, esp. on mobile?
3. Is the **Alerts tab** form laid out correctly (the regression class this exists to catch)?
4. Vote accordingly.

## Follow-ups (out of scope for the first cut)

- Inline screenshot **grid** in the PR comment (currently an artifact link).
- Pixel-diff baselines / approval gating.
- A dedicated runner label if the shared runner gets noisy.
