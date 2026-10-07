# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities **privately** — do not open a public
issue or pull request.

Email **security@superstatus.io** with:

- a description of the issue and its impact,
- steps to reproduce (proof-of-concept if you have one),
- affected version / commit.

We aim to acknowledge reports within a few business days and will keep you
updated on remediation. Please give us a reasonable window to release a fix
before any public disclosure.

## Supported versions

SuperStatus is pre-1.0; security fixes land on `main`. Self-hosters should track
the latest release.

## Self-hosting hardening

The default `docker compose` stack uses plain HTTP, demo secrets, and OpenIddict
development signing keys. What you need depends on the network:

- **Localhost, or a LAN you control** — plain HTTP by IP is fine (the `start.sh`
  server-by-IP path). The one thing you must still do is **regenerate the
  secrets** `POSTGRES_PASSWORD` and `OIDC_WEB_CLIENT_SECRET`
  (`openssl rand -base64 24` — `start.sh` does this automatically).
- **An untrusted network or the public internet** — additionally **terminate TLS
  at a reverse proxy** in front of `web` and `identity`. Never expose the
  plain-HTTP, relaxed-cookie stack to a network you don't control.

See [`docs/self-hosting.md`](docs/self-hosting.md) for the full runbook. These
trade-offs are intentional and documented so there are no surprises.
