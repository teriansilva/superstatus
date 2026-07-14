#!/bin/sh
#
# SuperStatus one-line installer.
#
#   curl -fsSL https://superstatus.superstatus.io/install.sh | sh
#
# Installs the full SuperStatus status-page stack on this host by PULLING the
# latest released images from GHCR (no source checkout, no build) and bringing
# them up with Docker Compose. It auto-detects this host's IP, generates secrets,
# and serves plain HTTP on your LAN — fine for a trusted network. To expose it on
# the internet, put it behind a TLS reverse proxy (see docs/self-hosting.md).
#
# What it does:
#   1. Checks for Docker + the Compose plugin.
#   2. Creates an install directory (default ./superstatus, override with
#      SUPERSTATUS_DIR).
#   3. Writes docker-compose.yml, postgres/init-databases.sh, and a .env with
#      freshly generated secrets (mode 0600).
#   4. docker compose pull && docker compose up -d.
#   5. Prints the URL to open and the day-2 commands.
#
# Options:
#   --no-updater       omit the Watchtower update engine (nothing mounts the
#                      Docker socket; the console shows the guided command).
#   --auto-update      deprecated no-op — auto-update is now a runtime toggle in
#                      the admin console, not an install-time choice.
#
# Environment overrides (all optional):
#   SUPERSTATUS_DIR    install directory                 (default ./superstatus)
#   SUPERSTATUS_VERSION image tag to pull                (default latest)
#   HOST_IP            advertised IP / hostname          (default auto-detect)
#   WEB_PORT           published web port                (default 8080)
#   IDENTITY_PORT      published identity port           (default 8081)
#   BIND_ADDR          interface to bind published ports (default 0.0.0.0)
#   SEED_SAMPLE_DATA   seed demo checks on first run     (default true)
#   SUPERSTATUS_UPDATE_ENGINE  watchtower | none         (default watchtower)
#
# Issue #334: the update engine ships by DEFAULT, so "Update now" and the
# auto-update schedule work from the web console with no server access. Only the
# Watchtower container mounts the Docker socket — web/api never do; they call its
# authenticated http-api. Whether auto-update runs, and when, is a persisted
# setting in the console, not a flag here.
#
# Re-running in an existing install dir reuses the existing .env (delete it to
# regenerate secrets) and just pulls + restarts.
set -eu

say()  { printf '%s\n' "$*"; }
err()  { printf 'error: %s\n' "$*" >&2; }
die()  { err "$*"; exit 1; }

# #334: which update engine to install. "watchtower" (default) ships the
# on-demand updater so the console's auto-update toggle + "Update now" work with
# no server access; "none" omits it entirely — nothing mounts the Docker socket.
# Empty means "decide from the existing .env" when re-running an install.
UPDATE_ENGINE="${SUPERSTATUS_UPDATE_ENGINE:-}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --no-updater)
      UPDATE_ENGINE=none
      ;;
    --auto-update|--no-auto-update)
      # Deprecated (#334). Auto-update used to be an install-time overlay; it is
      # now a runtime toggle in the console, so these flags no longer choose
      # anything. Accepted so existing scripts/docs keep working.
      say "note: $1 is deprecated and ignored — auto-update is now a toggle in the"
      say "      admin console (Updates panel). Use --no-updater to omit the update"
      say "      engine entirely."
      ;;
    -h|--help)
      cat <<'HELP'
Usage: install.sh [--no-updater]

Options:
  --no-updater      Omit the Watchtower update engine. Nothing mounts the Docker
                    socket, the console's "Update now" button and auto-update
                    schedule are unavailable, and the panel shows the guided
                    upgrade command instead. Same as SUPERSTATUS_UPDATE_ENGINE=none.

Deprecated (accepted, ignored):
  --auto-update     Auto-update is no longer chosen at install time. The engine
  --no-auto-update  ships by default; turn automatic updates on/off and set the
                    daily time in the admin console (Updates panel).
HELP
      exit 0
      ;;
    *)
      die "Unknown option: $1"
      ;;
  esac
  shift
done

case "$UPDATE_ENGINE" in
  "") ;;
  watchtower) ;;
  none) ;;
  *) die "SUPERSTATUS_UPDATE_ENGINE must be 'watchtower' or 'none'." ;;
esac

VERSION="${SUPERSTATUS_VERSION:-latest}"
DIR="${SUPERSTATUS_DIR:-./superstatus}"
WEB_PORT="${WEB_PORT:-8080}"
IDENTITY_PORT="${IDENTITY_PORT:-8081}"
BIND_ADDR="${BIND_ADDR:-0.0.0.0}"
SEED_SAMPLE_DATA="${SEED_SAMPLE_DATA:-true}"

# ── 1. Preflight: Docker + Compose ────────────────────────────────────────────
# If Docker or the Compose plugin is missing, offer to install the current
# version via Docker's official convenience script (https://get.docker.com),
# which installs Docker Engine + the Compose & Buildx plugins. Behaviour:
#   SUPERSTATUS_INSTALL_DOCKER=yes  → install without asking
#   SUPERSTATUS_INSTALL_DOCKER=no   → never install (just fail with a link)
#   unset + a terminal              → ask
# Installing needs root, so we use sudo when not already root.

as_root() { if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi; }

confirm_install_docker() {
  case "${SUPERSTATUS_INSTALL_DOCKER:-}" in
    y|Y|yes|YES|true|1) return 0 ;;
    n|N|no|NO|false|0)  return 1 ;;
  esac
  if [ -r /dev/tty ]; then
    printf '%s [y/N] ' "$1" > /dev/tty
    read -r _ans < /dev/tty 2>/dev/null || _ans=""
    case "$_ans" in y|Y|yes|YES) return 0 ;; *) return 1 ;; esac
  fi
  return 1   # non-interactive and no override → don't auto-install
}

install_docker() {
  command -v curl >/dev/null 2>&1 || die "curl is required to auto-install Docker."
  if [ "$(id -u)" -ne 0 ] && ! command -v sudo >/dev/null 2>&1; then
    die "Installing Docker needs root, but neither root nor sudo is available. Install it manually: https://docs.docker.com/engine/install/"
  fi
  say "Installing Docker Engine + Compose via https://get.docker.com ..."
  _tmp="$(mktemp)"
  curl -fsSL https://get.docker.com -o "$_tmp" || { rm -f "$_tmp"; die "Could not download the Docker install script."; }
  as_root sh "$_tmp" || { rm -f "$_tmp"; die "Automatic Docker install failed. Install it manually: https://docs.docker.com/engine/install/"; }
  rm -f "$_tmp"
  as_root systemctl enable --now docker >/dev/null 2>&1 || true
  say "Docker installed."
}

if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  _what="Docker"; command -v docker >/dev/null 2>&1 && _what="the Docker Compose plugin"
  say "${_what} is not installed."
  if confirm_install_docker "Install Docker Engine + Compose now (official get.docker.com script, uses sudo)?"; then
    install_docker
  else
    die "SuperStatus needs Docker + the Compose plugin. Install them and re-run, or set SUPERSTATUS_INSTALL_DOCKER=yes — https://docs.docker.com/engine/install/"
  fi
fi

# Re-verify after a possible install.
command -v docker >/dev/null 2>&1 || die "Docker is still not found after install."
docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin is still missing after install."

# Decide whether we need sudo to reach the daemon. A docker-group membership the
# installer just added isn't active in this shell yet, so fall back to
# 'sudo docker' for this run (and tell the user how to drop sudo next time).
DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
  if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
    DOCKER="sudo docker"
    say "Using 'sudo docker' for this run. To use Docker without sudo later:"
    say "  sudo usermod -aG docker \"\$USER\"   (then log out and back in)"
  else
    die "Cannot talk to the Docker daemon. Is it running, and can this user reach it (docker group / sudo)?"
  fi
fi

# #358: the OIDC login redirect now FOLLOWS whatever host the browser opens the app
# at (an IP, a hostname, localhost) — nothing is baked into .env (the issuer is
# pinned to the internal authority) — so HOST_IP is only a display hint for the
# "open this URL" line at the end. A wrong guess no longer strands the login
# redirect, so there is no prompt and no private-IP warning. Override HOST_IP just
# to change the printed hint.
HOST_IP="${HOST_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
[ -n "$HOST_IP" ] || HOST_IP="localhost"

gen_secret() { openssl rand -base64 24 2>/dev/null || head -c 18 /dev/urandom | base64; }

# Set KEY=VALUE in ./.env, replacing an existing line or appending a new one.
# Rewrites via a temp file under umask 077 so the generated secrets already in
# .env never widen their mode. Values here are installer-controlled (no '|').
env_upsert() {
  _k="$1"; _v="$2"
  if grep -q "^${_k}=" .env 2>/dev/null; then
    _tmp=$(mktemp 2>/dev/null || echo ".env.tmp.$$")
    ( umask 077; sed "s|^${_k}=.*|${_k}=${_v}|" .env > "$_tmp" )
    mv "$_tmp" .env
  else
    ( umask 077; printf '%s=%s\n' "$_k" "$_v" >> .env )
  fi
}

# Read KEY's value from ./.env (empty when absent).
env_get() { sed -n "s|^$1=||p" .env 2>/dev/null | head -n1; }

# ── 2. Install directory ──────────────────────────────────────────────────────
say "Installing SuperStatus (${VERSION}) into ${DIR} ..."
mkdir -p "$DIR/postgres"
cd "$DIR"

# ── 3a. docker-compose.yml ────────────────────────────────────────────────────
# Pull-based stack. Quoted heredoc — ${...} / $$ are written literally for
# Compose to interpolate at runtime, not expanded by this installer's shell.
cat > docker-compose.yml <<'COMPOSE_EOF'
name: superstatus

# Self-host stack for SuperStatus, written by install.sh.
# PULLS prebuilt images from GHCR (no build) and runs the whole status page on
# plain HTTP. Configuration lives in the adjacent .env file.
#
# ── How single-host OIDC works here ───────────────────────────────────────────
# Web and Identity speak OIDC, which needs the OIDC authority reachable by BOTH
# the browser (login redirects) and the web/api containers (back-channel
# discovery / token / JWKS). #358: the browser-facing login redirect FOLLOWS the
# host you open the app at (nothing baked in; the issuer is pinned to IDP_HTTP, the
# internal compose-network address the servers use). Pin your public host after
# first run in the console (Settings -> Access & security). See docs/self-hosting.md.

x-aspnet-env: &aspnet-env
  ASPNETCORE_ENVIRONMENT: Production
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"

x-healthcheck: &http-health
  test: ["CMD", "curl", "-fsS", "http://localhost:8080/health"]
  interval: 10s
  timeout: 5s
  retries: 18
  start_period: 90s

services:
  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    command: ["postgres", "-c", "max_connections=200"]
    environment:
      POSTGRES_USER: ${POSTGRES_USER:-superstatus}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-superstatus_local_dev}
      POSTGRES_DB: postgres
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./postgres/init-databases.sh:/docker-entrypoint-initdb.d/init-databases.sh:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $${POSTGRES_USER} -d postgres"]
      interval: 10s
      timeout: 5s
      retries: 5
    networks: [internal]

  identity:
    image: ghcr.io/teriansilva/superstatus-identity:${SUPERSTATUS_VERSION:-latest}
    restart: unless-stopped
    labels:
      com.centurylinklabs.watchtower.enable: "true"
      com.centurylinklabs.watchtower.scope: "superstatus"
    environment:
      <<: *aspnet-env
      ConnectionStrings__SuperStatusIdentityDb: "Host=postgres;Database=SuperStatusIdentityDb;Username=${POSTGRES_USER:-superstatus};Password=${POSTGRES_PASSWORD:-superstatus_local_dev};Maximum Pool Size=20;Connection Idle Lifetime=30"
      # #358: dynamic issuer by default. Set WEBAPP_HTTP / IDP_PUBLIC_HTTP in .env for
      # a reverse-proxy / pinned deploy — the null-map form (no value) forwards them
      # from .env when set and OMITS them (⇒ dynamic self-host) when unset.
      WEB_PORT: ${WEB_PORT:-8080}
      API_INTERNAL_HTTP: http://api:8080
      # #358 (ID2088): internal authority Identity pins as its dynamic-mode issuer so
      # the front-channel authorize and back-channel token endpoint agree on it.
      IDP_HTTP: ${IDP_HTTP:-http://identity:8080}
      WEBAPP_HTTP:
      IDP_PUBLIC_HTTP:
      OIDC_WEB_CLIENT_SECRET: ${OIDC_WEB_CLIENT_SECRET:-superstatus_local_dev_oidc}
      APPLY_MIGRATIONS: "true"
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck: *http-health
    ports:
      - "${BIND_ADDR:-127.0.0.1}:${IDENTITY_PORT:-8081}:8080"
    networks: [internal]

  api:
    image: ghcr.io/teriansilva/superstatus-api:${SUPERSTATUS_VERSION:-latest}
    restart: unless-stopped
    labels:
      com.centurylinklabs.watchtower.enable: "true"
      com.centurylinklabs.watchtower.scope: "superstatus"
    environment:
      <<: *aspnet-env
      ConnectionStrings__SuperStatusDb: "Host=postgres;Database=SuperStatusDb;Username=${POSTGRES_USER:-superstatus};Password=${POSTGRES_PASSWORD:-superstatus_local_dev};Maximum Pool Size=20;Connection Idle Lifetime=30"
      IDP_HTTP: ${IDP_HTTP:-http://identity:8080}
      IDP_PUBLIC_HTTP:
      APPLY_MIGRATIONS: "true"
      # #334: docker-compose.watchtower.yml layers the update-engine trigger URL +
      # token onto this service. Absent ⇒ no engine ⇒ the console shows the guided
      # command. The auto-update toggle/time is a persisted setting, not an env var.
      SEED_SAMPLE_DATA: ${SEED_SAMPLE_DATA:-true}
    depends_on:
      postgres:
        condition: service_healthy
      identity:
        condition: service_healthy
    healthcheck: *http-health
    networks: [internal]

  web:
    image: ghcr.io/teriansilva/superstatus-web:${SUPERSTATUS_VERSION:-latest}
    restart: unless-stopped
    labels:
      com.centurylinklabs.watchtower.enable: "true"
      com.centurylinklabs.watchtower.scope: "superstatus"
    environment:
      <<: *aspnet-env
      IDP_HTTP: ${IDP_HTTP:-http://identity:8080}
      IDENTITY_PORT: ${IDENTITY_PORT:-8081}
      WEBAPP_HTTP:
      IDP_PUBLIC_HTTP:
      OIDC_WEB_CLIENT_SECRET: ${OIDC_WEB_CLIENT_SECRET:-superstatus_local_dev_oidc}
      services__apiservice__http__0: "http://api:8080"
    depends_on:
      api:
        condition: service_healthy
      identity:
        condition: service_healthy
    healthcheck: *http-health
    ports:
      - "${BIND_ADDR:-127.0.0.1}:${WEB_PORT:-8080}:8080"
    networks: [internal]

volumes:
  pgdata:

networks:
  internal:
    driver: bridge
COMPOSE_EOF

# #334: the update engine. Always written to disk (so `--no-updater` installs can
# switch it on later without re-downloading the installer), but only wired into
# COMPOSE_FILE — and therefore only ever started — when the engine is "watchtower".
cat > docker-compose.watchtower.yml <<'WATCHTOWER_COMPOSE_EOF'
name: superstatus

# SuperStatus update engine (issue #334) — part of the default stack.
#
# .env's COMPOSE_FILE lists this file alongside docker-compose.yml, so the day-2
# commands (`docker compose pull && docker compose up -d`) include it with no -f
# flags. Re-run `install.sh --no-updater` to drop it: the Watchtower service is
# then never created and nothing mounts the Docker socket.
#
# Security boundary: Watchtower is the ONLY container that mounts
# /var/run/docker.sock (root-equivalent access to this host). The web/api
# containers never touch it — they ask Watchtower to pull + recreate over its
# authenticated http-api (shared bearer token SUPERSTATUS_UPDATE_TOKEN). The
# http-api port is internal to the compose network; it is not published.
#
# Watchtower is a pure on-demand executor: http-api enabled, no WATCHTOWER_SCHEDULE
# and no periodic polls. The app owns the cadence — set the auto-update toggle and
# daily UTC time in the admin console. Exactly one scheduler, so "off" means off.

services:
  api:
    environment:
      SUPERSTATUS_UPDATE_TRIGGER_URL: http://watchtower:8080/v1/update
      SUPERSTATUS_UPDATE_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?set SUPERSTATUS_UPDATE_TOKEN in .env (re-run install.sh)}

  watchtower:
    image: containrrr/watchtower:latest
    restart: unless-stopped
    environment:
      WATCHTOWER_CLEANUP: "true"
      WATCHTOWER_LABEL_ENABLE: "true"
      WATCHTOWER_NO_STARTUP_MESSAGE: "true"
      WATCHTOWER_SCOPE: "superstatus"
      # On-demand only: WATCHTOWER_HTTP_API_UPDATE suppresses the poll loop unless
      # WATCHTOWER_HTTP_API_PERIODIC_POLLS is also set — deliberately absent, as is
      # WATCHTOWER_SCHEDULE.
      WATCHTOWER_HTTP_API_UPDATE: "true"
      WATCHTOWER_HTTP_API_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?set SUPERSTATUS_UPDATE_TOKEN in .env (re-run install.sh)}
      TZ: "${TZ:-UTC}"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    # Must share a network with `api`, which calls the http-api by service name at
    # http://watchtower:8080. Without this, Compose puts watchtower on the implicit
    # `default` network while api stays on `internal`, and the trigger fails DNS
    # resolution (#380). The port is still never published to the host.
    networks: [internal]
WATCHTOWER_COMPOSE_EOF

# ── 3b. postgres/init-databases.sh ────────────────────────────────────────────
cat > postgres/init-databases.sh <<'INITDB_EOF'
#!/bin/bash
# Mounted into /docker-entrypoint-initdb.d/ — runs once on a fresh data dir.
# Creates the two logical databases SuperStatus expects to exist.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "${POSTGRES_DB:-postgres}" <<-EOSQL
    CREATE DATABASE "SuperStatusDb";
    CREATE DATABASE "SuperStatusIdentityDb";
EOSQL
INITDB_EOF
chmod +x postgres/init-databases.sh

# ── 3b2. Resolve the update engine (#334) ─────────────────────────────────────
# Precedence: explicit flag / env > the engine recorded in an existing .env >
# the default ("watchtower"). Re-running without a flag therefore preserves an
# operator's earlier --no-updater choice rather than silently re-adding the
# socket mount.
if [ -z "$UPDATE_ENGINE" ]; then
  UPDATE_ENGINE=$(env_get SUPERSTATUS_UPDATE_ENGINE)
  [ -n "$UPDATE_ENGINE" ] || UPDATE_ENGINE=watchtower
fi

# COMPOSE_FILE is read by `docker compose` straight out of .env, so the bare
# day-2 commands (`docker compose pull && docker compose up -d`) pick up the
# updater without any -f flags — and omit it entirely when opted out.
if [ "$UPDATE_ENGINE" = none ]; then
  COMPOSE_FILE_VALUE="docker-compose.yml"
else
  COMPOSE_FILE_VALUE="docker-compose.yml:docker-compose.watchtower.yml"
fi

# ── 3c. .env (generated secrets, owner-only) ──────────────────────────────────
if [ ! -f .env ]; then
  say "Writing .env (LAN access on ${HOST_IP}, generated secrets) ..."
  # .env holds POSTGRES_PASSWORD / OIDC_WEB_CLIENT_SECRET — create it owner-only
  # (umask 077 -> mode 0600) so other local users can't read the secrets.
  ( umask 077
    cat > .env <<EOF
# Generated by install.sh — SuperStatus reachable on the LAN by IP.
# Plain HTTP on the LAN. For internet exposure use a TLS proxy (docs/self-hosting.md).
SUPERSTATUS_VERSION=${VERSION}
BIND_ADDR=${BIND_ADDR}
WEB_PORT=${WEB_PORT}
IDENTITY_PORT=${IDENTITY_PORT}
# #358: no WEBAPP_HTTP / IDP_PUBLIC_HTTP — the login redirect follows the host the
# browser uses (the issuer is pinned to the internal authority). Pin your public
# address after first run in the console (Settings -> Access & security). Behind a
# TLS proxy? See docs/self-hosting.md.
IDP_HTTP=http://identity:8080
POSTGRES_USER=superstatus
POSTGRES_PASSWORD=$(gen_secret)
OIDC_WEB_CLIENT_SECRET=$(gen_secret)
SEED_SAMPLE_DATA=${SEED_SAMPLE_DATA}
# #334: the update engine. "watchtower" (default) lets the admin console apply
# updates and run them on a schedule with no server access; "none" omits it
# (re-run install.sh --no-updater). Only the Watchtower container ever mounts the
# Docker socket. COMPOSE_FILE is what makes the choice stick for the bare
# \`docker compose\` day-2 commands below.
SUPERSTATUS_UPDATE_ENGINE=${UPDATE_ENGINE}
COMPOSE_FILE=${COMPOSE_FILE_VALUE}
# Shared bearer token for Watchtower's http-api. Never leaves the host; the api
# sends it to Watchtower over the internal network only.
SUPERSTATUS_UPDATE_TOKEN=$(gen_secret)
EOF
  )
  chmod 600 .env 2>/dev/null || true
else
  say "Using existing .env (delete it to regenerate secrets) — pinning version ${VERSION}."
  # Re-pin SUPERSTATUS_VERSION to the version requested on this run so the day-2
  # `docker compose pull/up` commands (and future restarts) use it too — not just
  # this invocation. Rewrite in place via a temp file to preserve the generated
  # secrets already in .env; keep it owner-only (mode 0600).
  env_upsert SUPERSTATUS_VERSION "$VERSION"
  # Issue #311: ensure a Watchtower http-api token exists for the console's
  # "Update now" button. Generated once and left stable on re-run — never
  # regenerated, so the api ↔ Watchtower shared secret keeps matching.
  if ! grep -q '^SUPERSTATUS_UPDATE_TOKEN=' .env 2>/dev/null; then
    ( umask 077; printf 'SUPERSTATUS_UPDATE_TOKEN=%s\n' "$(gen_secret)" >> .env )
  fi
  chmod 600 .env 2>/dev/null || true
fi

# #334: record the engine choice and make it stick for the bare day-2 compose
# commands. Upserted on every run (fresh .env included) so an install predating
# COMPOSE_FILE picks it up, and so `--no-updater` on a re-run genuinely drops the
# updater file rather than leaving a stale COMPOSE_FILE behind.
env_upsert SUPERSTATUS_UPDATE_ENGINE "$UPDATE_ENGINE"
env_upsert COMPOSE_FILE "$COMPOSE_FILE_VALUE"
chmod 600 .env 2>/dev/null || true

# ── 4. Pull + up ──────────────────────────────────────────────────────────────
# No -f flags: `docker compose` resolves COMPOSE_FILE from the .env just written,
# so the installer exercises exactly the file set the operator's day-2 commands
# will use. --remove-orphans makes `--no-updater` on a re-run actually tear down a
# Watchtower container left by a previous install.
if [ "$UPDATE_ENGINE" = none ]; then
  say "Update engine: none (--no-updater) — nothing will mount the Docker socket."
else
  say "Update engine: watchtower (on-demand; auto-update is off until you enable it in the console)."
fi

say "Pulling images ..."
$DOCKER compose pull
say "Starting SuperStatus (first run initializes the database) ..."
$DOCKER compose up -d --remove-orphans

# ── 5. Done ───────────────────────────────────────────────────────────────────
cat <<EOF

SuperStatus is starting. From any machine on your network, open:

    http://${HOST_IP}:${WEB_PORT}

...or any other address this host answers at — the login adapts to it. Make sure
BOTH the web port (${WEB_PORT}) and the identity port (${IDENTITY_PORT}) are reachable
from your browser (open them in your firewall / cloud NSG).

The first visit creates your administrator account. Then pin your public address
in the console (Settings -> Access & security) to harden sign-in.

Installed in:  $(pwd)
Logs:    ${DOCKER} compose logs -f
Update:  open the console → Updates → "Update now" (no server access needed)
         or run: ${DOCKER} compose pull && ${DOCKER} compose up -d
Auto-update: $([ "$UPDATE_ENGINE" = none ] \
  && printf 'unavailable (--no-updater; nothing mounts the Docker socket)' \
  || printf 'off — turn it on and pick a daily time in the console (Updates)')
Stop:    ${DOCKER} compose down        (add -v to also wipe the database)

Serving plain HTTP for a trusted LAN. To expose SuperStatus on the internet,
front it with a TLS reverse proxy — see docs/self-hosting.md.
EOF
