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
in. No `.env` edits, and no hosts-file entry — the login server is reached
directly at `localhost:8081`.

> Prefer to build from source instead of pulling? Layer the build override:
> `docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build`.

### How the OIDC wiring works (dynamic issuer)

Web and Identity speak OIDC, and the login flow must be reachable by both the
browser (login redirects) and the containers (back-channel discovery / token /
JWKS). Rather than bake a fixed public host into the config, SuperStatus **follows
the host you open the app at for the browser-facing login redirect**, while the
OIDC issuer itself is a stable internal value (so the authorize and token
endpoints always agree on it):

| Channel | Reaches | How |
|---|---|---|
| Browser (front-channel) | Web on `:8080`, Identity on `:8081` | at whatever host you opened — Web rebuilds the login **redirect** to that host on the identity port |
| Containers (back-channel) | `IDP_HTTP = http://identity:8080` | the internal compose-network address for discovery / token / JWKS, and the pinned issuer |

So an IP, a hostname, or `localhost` all just work with no configuration. On
first run any host is accepted (relaxed); once you're in, pin your public
address(es) under **Settings → Access & security** to restrict which hosts may
drive sign-in.

## 2. Run on a server, reached by IP or hostname

Most deployments run on a separate box reached over the network. The defaults
bind to loopback only, so for network access bind to all interfaces. The helper
script generates secrets and starts everything:

```bash
./start.sh
# then open http://<server-ip>:8080 from any machine that can reach it
```

**There is no host to configure.** The browser-facing login (OIDC) redirect
follows whatever address you actually open the app at — a LAN IP, a cloud VM's
public IP, a hostname — so a NAT'd cloud VM (where `hostname -I` only sees the
private NIC, e.g. `10.0.0.4`) needs no special handling; open the app at the
public IP and sign-in follows it. `HOST_IP=…` only changes the URL the script
prints at the end.

The one requirement: the browser must reach **both** published ports directly —
the web port (`8080`) **and** the identity port (`8081`). On a cloud VM, open
both in the security group / NSG; behind a host firewall, allow both. (A login
that redirects to `…:8081` and times out is almost always a closed identity port.)

To do it by hand instead of `start.sh`, write a `.env`:

```ini
BIND_ADDR=0.0.0.0
IDP_HTTP=http://identity:8080
POSTGRES_PASSWORD=<openssl rand -base64 24>
OIDC_WEB_CLIENT_SECRET=<openssl rand -base64 24>
```

then `docker compose up -d`. After first-run setup, **harden the issuer**: open
**Settings → Access & security** and add the public address(es) users reach this
server at (an IP or a hostname, optionally `host:port`). Until you do, a banner in
the console reminds you the issuer is unpinned (any host is accepted).

> This is **plain HTTP** — appropriate for a trusted network only. For internet
> exposure, terminate TLS at a reverse proxy (§5) instead.

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

**The Docker socket.** A default install runs the update engine (§6), and that
Watchtower container mounts `/var/run/docker.sock` — root-equivalent control of the
host. This is what lets you update from the web with no server access. It is scoped
to the SuperStatus app containers, its http-api is token-authenticated and never
published to the host, and **the `web`, `api` and `identity` containers never get
socket access**. If that trade isn't right for you, install with `--no-updater`
(§6) and update by hand.

See also [`SECURITY.md`](../SECURITY.md).

## 5. Exposing on your own domain (TLS reverse proxy)

For a real deployment, front Web **and** Identity with a reverse proxy that
terminates TLS, give each a hostname, and point the app at the **public HTTPS
URLs**.

> **Setting `WEBAPP_HTTP` selects the reverse-proxy path.** The self-host default
> leaves `WEBAPP_HTTP` and `IDP_PUBLIC_HTTP` unset — the dynamic two-port issuer of
> §1–§2. Set `WEBAPP_HTTP` to your public URL to serve the whole stack behind one
> TLS hostname instead: the issuer is taken from the forwarded `Host` header
> (`ASPNETCORE_FORWARDEDHEADERS_ENABLED` is already on) and the secure path
> (Secure / `SameSite=None` cookies, `form_post`) applies. Optionally also set
> `IDP_PUBLIC_HTTP` to pin a fixed issuer explicitly. As a safety net the services
> **refuse to start** if `IDP_PUBLIC_HTTP` is a localhost URL while `IDP_HTTP` is HTTPS.

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

Updating is a web task, not a server task. Open the console → **Updates**:

- **Update now** — pulls the new images and restarts the app containers.
- **Automatic updates** — a toggle plus a daily time (UTC). Off by default.

Neither needs SSH, a shell, or a copy-pasted command. The setting is persisted in
the database, so it survives restarts and takes effect without a redeploy.

If you'd rather update by hand (or you installed with `--no-updater`, below), the
stack pulls prebuilt images, so it's just a pull — no source checkout, no build:

```bash
docker compose pull
docker compose up -d
```

`SUPERSTATUS_VERSION` (in `.env`) selects the tag: `latest` tracks the newest
release; pin a specific tag (e.g. `v1.2.0`) for a reproducible deploy.

### How it works, and why it's safe

`install.sh` ships an **update engine** by default: a `containrrr/watchtower`
container, wired in through `COMPOSE_FILE` in `.env`, so a bare
`docker compose up -d` includes it.

**Watchtower is the only container that mounts `/var/run/docker.sock`** — which is
root-equivalent control of the Docker host. The SuperStatus `web`, `api` and
`identity` containers **never** receive Docker socket access. When you press
"Update now" (or the schedule fires), the api makes an authenticated HTTP call to
`http://watchtower:8080/v1/update` with the shared `SUPERSTATUS_UPDATE_TOKEN`
(generated in `.env`, never sent to the browser, never logged). That port is
reachable only on the internal compose network.

Watchtower runs as a pure **on-demand executor**: it has no schedule of its own and
does not poll. The app is the only scheduler, which is what makes the console's
toggle authoritative — switching automatic updates off really stops them.

Its label and scope filters mean only the SuperStatus app containers are recreated;
**Postgres is never auto-updated**. `--cleanup` removes the superseded app images
after a successful update, so the previous image stays available for a manual
rollback until then.

An update recreates the app containers. Existing admin sessions briefly disconnect
and reconnect while the new containers start.

### Opting out of the update engine

If you don't want anything on the host mounting the Docker socket:

```bash
./install.sh --no-updater          # or: SUPERSTATUS_UPDATE_ENGINE=none ./install.sh
```

This omits the Watchtower service entirely (and removes it if a previous install
started one). The console then hides the auto-update toggle and the "Update now"
button, and shows the guided `docker compose pull && docker compose up -d` command
instead. Re-running `install.sh` with no flag preserves your choice; pass
`SUPERSTATUS_UPDATE_ENGINE=watchtower` to switch the engine back on.

### Upgrading from an older install

Installs made before the engine shipped by default used an opt-in overlay
(`install.sh --auto-update`) and a Watchtower cron schedule (`WATCHTOWER_SCHEDULE`).
Re-run `install.sh` to migrate: it adds `COMPOSE_FILE` and
`SUPERSTATUS_UPDATE_ENGINE` to your `.env`, keeps your existing
`SUPERSTATUS_UPDATE_TOKEN` (it is never regenerated), and drops the cron schedule in
favour of the console's toggle. Automatic updates start **off** — turn them on in
the console when you want them. The old `--auto-update` / `--no-auto-update` flags
are still accepted but now do nothing; leftover `SUPERSTATUS_AUTOUPDATE` and
`WATCHTOWER_SCHEDULE` lines in `.env` are unused and can be deleted.

**Building from source instead** (contributors): layer the build override —

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

EF Core migrations apply automatically on startup (`APPLY_MIGRATIONS` defaults
to `true`).

## 7. Backup & restore

Application data lives in the `pgdata` Docker volume. Two small extra volumes,
`identity-dpkeys` and `web-dpkeys`, hold each service's ASP.NET DataProtection
keyring so logins and form submissions keep working across container recreates
(updates, restarts). They contain only key material — no application data — and
are recreated automatically if removed (everyone simply re-logs in once).

```bash
# Backup
docker compose exec postgres pg_dumpall -U "$POSTGRES_USER" > superstatus-backup.sql

# Restore (into a fresh stack)
cat superstatus-backup.sql | docker compose exec -T postgres psql -U "$POSTGRES_USER" -d postgres
```

`docker compose down` keeps the volume; `docker compose down -v` **deletes** it.

## 8. Uninstalling

To remove SuperStatus completely, the one-liner counterpart to the installer:

```bash
curl -fsSL https://superstatus.superstatus.io/uninstall.sh | sh
# …or, from your install directory:
sh uninstall.sh
```

It removes the containers, the `pgdata` volume (**all status history and
accounts**), the compose network, the pulled images, and the install files
(`docker-compose.yml`, `.env`, `postgres/init-databases.sh`). It asks for an
explicit `yes` first, and **leaves Docker itself installed**. Overrides:
`SUPERSTATUS_DIR` (custom install dir), `SUPERSTATUS_YES=1` (skip the prompt /
run non-interactively), `SUPERSTATUS_KEEP_IMAGES=1`, `SUPERSTATUS_KEEP_DIR=1`.

Prefer to do it by hand? From the install directory:

```bash
docker compose down -v --remove-orphans      # containers + the pgdata volume (DELETES data) + network
docker rmi ghcr.io/teriansilva/superstatus-web \
           ghcr.io/teriansilva/superstatus-api \
           ghcr.io/teriansilva/superstatus-identity \
           postgres:16-alpine                # drop the images (optional)
cd .. && rm -rf superstatus                  # remove the compose file, .env, init script
```

`down` without `-v` keeps your database; `-v` is what wipes it.

## 9. Recovering a forgotten admin password

Password reset by email is intentionally disabled (no mail dependency). To
recover, clear the user table and re-run first-run setup:

```bash
docker compose exec postgres \
  psql -U "$POSTGRES_USER" -d SuperStatusIdentityDb -c \
  'DELETE FROM "AspNetUserRoles"; DELETE FROM "AspNetUsers";'
```

The next visit to the admin area redirects to the setup wizard again; the OIDC
client and role rows are untouched.

## 10. Known limitations of the self-host stack

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
- **The update engine holds the Docker socket.** A default install runs Watchtower
  with `/var/run/docker.sock` mounted, so the console can update the stack without
  server access (§6). Only that container gets the socket, its http-api is
  token-authenticated and unpublished, and its scope filter excludes Postgres — but
  a compromise of it is a host compromise. Install with `--no-updater` to omit it.
- **The auto-update schedule is UTC only.** The console labels the time as UTC; it
  does not yet accept or display a local timezone.
- **No outbound email**; password-reset and email notifications are disabled.
