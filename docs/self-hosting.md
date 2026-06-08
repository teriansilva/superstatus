# Self-hosting SuperStatus

This guide covers running SuperStatus yourself — from a one-command local trial
to a TLS-fronted deployment on your own domain.

> The org-internal staging/production deploys (`docker-compose.staging.yml` /
> `docker-compose.prod.yml`) are a separate, reverse-proxy-fronted topology and
> are **not** what this guide describes. Use `docker-compose.yml`.

## 1. Local trial (plain HTTP, localhost)

**Prerequisites:** Docker 20.10+ with Compose v2.

```bash
git clone https://git.superstatus.io/superstatus.io/superstatus.git
cd superstatus
docker compose up -d     # pulls the prebuilt images — no local build
```

Open **http://localhost:8080**, complete the first-run admin setup, and you're
in. The localhost defaults need no `.env` edits.

> Prefer to build from source instead of pulling? Layer the build override:
> `docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build`.

If your browser doesn't auto-resolve `*.localhost` (older Safari), add
`127.0.0.1   id.localhost` to your hosts file.

### How the localhost OIDC wiring works

Web and Identity speak OIDC. The authority has to be reachable identically by
the browser and by the containers, which can't share one hostname on a plain
localhost host (browsers need `*.localhost`; glibc inside the containers pins
`*.localhost` to loopback). SuperStatus bridges this with two variables:

| Variable | Used by | Points at |
|---|---|---|
| `IDP_PUBLIC_HTTP` | browser (front-channel) | `http://id.localhost:8081` |
| `IDP_HTTP` | containers (back-channel) | `http://identity:8080` |

Identity pins `IDP_PUBLIC_HTTP` as its issuer; Web/API use it as the OIDC
authority but route their own discovery/token/JWKS calls to `IDP_HTTP`,
preserving the public `Host` header so the issued endpoints stay browser-correct.
You don't need to touch any of this for localhost.

## 2. Run on a server, reached by IP (LAN)

Most deployments run on a separate box that people reach over the network. The
defaults bind to loopback only, so for LAN access you need to bind to all
interfaces and point the public URLs at the server's IP.

The easiest way is the helper script, which detects the IP, generates secrets,
and starts everything:

```bash
./start.sh
# then open http://<server-ip>:8080 from any machine on the LAN
```

To do it by hand instead, write a `.env` (substitute your server's IP):

```ini
BIND_ADDR=0.0.0.0
WEBAPP_HTTP=http://192.168.1.50:8080
IDP_PUBLIC_HTTP=http://192.168.1.50:8081
IDP_HTTP=http://identity:8080
POSTGRES_PASSWORD=<openssl rand -base64 24>
OIDC_WEB_CLIENT_SECRET=<openssl rand -base64 24>
```

then `docker compose up -d`. The browser and the containers agree on the issuer:
the browser uses the IP, and the containers rewrite their back-channel calls to
the internal `identity:8080`. Because Web and Identity share the same IP host,
there is no cross-site cookie issue.

> This is **plain HTTP** on your LAN — appropriate for a trusted network only.
> For internet exposure, terminate TLS at a reverse proxy (§5) instead.

## 3. Configuration

Every value has a working default; copy the template only to change something:

```bash
cp .env.example .env
```

See [`.env.example`](../.env.example) for the full list (ports, secrets, sample
data toggle).

## 4. Security — what's safe where

The defaults use **demo secrets** and **plain HTTP** with relaxed (non-Secure,
`SameSite=Lax`) auth cookies. What you need depends on the network:

- **Localhost, or a LAN you control** (a home/office network) — plain HTTP by IP
  is fine; this is the `start.sh` / §2 path. The one thing you must still do is
  **regenerate the secrets**: `start.sh` does it for you, or by hand —
  ```bash
  openssl rand -base64 24   # POSTGRES_PASSWORD
  openssl rand -base64 24   # OIDC_WEB_CLIENT_SECRET
  ```
- **An untrusted network or the public internet** — additionally put it behind a
  **TLS reverse proxy** (§5). Never expose the plain-HTTP, relaxed-cookie stack to
  a network you don't control.

See also [`SECURITY.md`](../SECURITY.md).

## 5. Exposing on your own domain (TLS reverse proxy)

For a real deployment, front Web **and** Identity with a reverse proxy that
terminates TLS, give each a hostname, and point the app at the **public HTTPS
URLs**.

> **Set `IDP_PUBLIC_HTTP` explicitly** to your public Identity URL. The compose
> file defaults it to the localhost value (`http://id.localhost:8081`), so
> *leaving it unset keeps the localhost issuer* and the plain-HTTP behaviour. Set
> it equal to `IDP_HTTP` and the secure path (Secure / `SameSite=None` cookies,
> `form_post`, issuer from the forwarded `Host` header — `ASPNETCORE_FORWARDEDHEADERS_ENABLED`
> is already on) applies. As a safety net the services **refuse to start** if
> `IDP_PUBLIC_HTTP` is a localhost URL while `IDP_HTTP` is HTTPS.

Example `.env` for `status.example.com` + `id.example.com`:

```ini
WEBAPP_HTTP=https://status.example.com
IDP_HTTP=https://id.example.com
IDP_PUBLIC_HTTP=https://id.example.com   # public Identity URL (same as IDP_HTTP)
POSTGRES_PASSWORD=<openssl rand -base64 24>
OIDC_WEB_CLIENT_SECRET=<openssl rand -base64 24>
```

Then bind the two published container ports to your proxy. Example Caddyfile:

```
status.example.com {
    reverse_proxy localhost:8080
}
id.example.com {
    reverse_proxy localhost:8081
}
```

Using the same registrable domain for both (`example.com`) keeps Web and
Identity same-site, so the default secure cookies work.

## 6. Updating

The stack pulls prebuilt images from the registry, so updating is just a pull —
no source checkout, no local build:

```bash
docker compose pull
docker compose up -d
```

`SUPERSTATUS_VERSION` (in `.env`) selects the tag: `latest` tracks the newest
release; pin a specific tag (e.g. `v1.2.0`) for a reproducible deploy.

**Building from source instead** (contributors): layer the build override —

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

EF Core migrations apply automatically on startup (`APPLY_MIGRATIONS` defaults
to `true`).

## 7. Backup & restore

Application data lives in the `pgdata` Docker volume.

```bash
# Backup
docker compose exec postgres pg_dumpall -U "$POSTGRES_USER" > superstatus-backup.sql

# Restore (into a fresh stack)
cat superstatus-backup.sql | docker compose exec -T postgres psql -U "$POSTGRES_USER" -d postgres
```

`docker compose down` keeps the volume; `docker compose down -v` **deletes** it.

## 8. Recovering a forgotten admin password

Password reset by email is intentionally disabled (no mail dependency). To
recover, clear the user table and re-run first-run setup:

```bash
docker compose exec postgres \
  psql -U "$POSTGRES_USER" -d SuperStatusIdentityDb -c \
  'DELETE FROM "AspNetUserRoles"; DELETE FROM "AspNetUsers";'
```

The next visit to the admin area redirects to the setup wizard again; the OIDC
client and role rows are untouched.

## 9. Known limitations of the self-host stack

These are the **knowingly-accepted** weak-by-design defaults of the trial stack,
catalogued in the pre-publish security review. They make the no-TLS,
single-host localhost/LAN trial work out of the box. None is safe to expose to
an untrusted network without the TLS reverse proxy in §5 — that is the dividing
line the whole "trusted LAN vs. internet" guidance in §4 is drawn around.

- **OpenIddict uses development signing/encryption certificates**, regenerated
  on container recreate — active OIDC sessions are invalidated on update until
  the cert/key persistence work lands. Operators re-login.
- **OIDC transport security is relaxed for the plain-HTTP path.** OpenIddict runs
  with `DisableTransportSecurityRequirement()` and the Web/API OIDC middleware
  sets `RequireHttpsMetadata = false`, so the issuer, metadata and token
  endpoints work over plain HTTP. This is what lets the localhost/LAN trial run
  without certificates; it also means an on-path attacker on an **untrusted**
  network could read or tamper with the auth handshake — hence the hard
  requirement to front Identity with TLS (§5) off a trusted LAN. Tightening this
  to require HTTPS metadata whenever a public HTTPS authority is configured is a
  tracked hardening follow-up.
- **The API does not validate the JWT audience** (`ValidateAudience = false`); it
  accepts any audience in a correctly-signed token from the bundled Identity
  issuer. Acceptable for the single-tenant self-host, where that Identity service
  is the only token issuer; per-audience validation is a hardening follow-up for
  multi-service deployments.
- **Plain-HTTP cookie relaxation** (non-Secure, `SameSite=Lax`) is applied only
  when the single-host plain-HTTP wiring is active; it is fine on a trusted LAN
  but unsafe on an untrusted network, which is why §4/§5 require a TLS proxy for
  untrusted/internet exposure.
- **Containers run as root** today.
- **No outbound email**; password-reset and email notifications are disabled.
