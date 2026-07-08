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
#   --auto-update      also install the opt-in Watchtower overlay.
#
# Environment overrides (all optional):
#   SUPERSTATUS_DIR    install directory                 (default ./superstatus)
#   SUPERSTATUS_VERSION image tag to pull                (default latest)
#   HOST_IP            advertised IP / hostname          (default auto-detect)
#   WEB_PORT           published web port                (default 8080)
#   IDENTITY_PORT      published identity port           (default 8081)
#   BIND_ADDR          interface to bind published ports (default 0.0.0.0)
#   SEED_SAMPLE_DATA   seed demo checks on first run     (default true)
#   WATCHTOWER_SCHEDULE six-field cron for auto-update   (default 0 0 3 * * *)
#
# Re-running in an existing install dir reuses the existing .env (delete it to
# regenerate secrets) and just pulls + restarts.
set -eu

say()  { printf '%s\n' "$*"; }
err()  { printf 'error: %s\n' "$*" >&2; }
die()  { err "$*"; exit 1; }

# Tri-state: true / false from an explicit flag or env override; empty means
# preserve the existing .env marker when re-running an install.
AUTO_UPDATE="${SUPERSTATUS_AUTO_UPDATE:-}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --auto-update)
      AUTO_UPDATE=true
      ;;
    --no-auto-update)
      AUTO_UPDATE=false
      ;;
    -h|--help)
      cat <<'HELP'
Usage: install.sh [--auto-update]

Options:
  --auto-update     Install the optional Watchtower overlay. This mounts the
                    Docker socket and lets Watchtower pull + recreate the
                    SuperStatus app containers on the configured nightly
                    schedule.
  --no-auto-update  Do not start Watchtower, even if SUPERSTATUS_AUTO_UPDATE or
                    an existing .env marker is set.
HELP
      exit 0
      ;;
    *)
      die "Unknown option: $1"
      ;;
  esac
  shift
done

case "$AUTO_UPDATE" in
  "") ;;
  y|Y|yes|YES|true|TRUE|1) AUTO_UPDATE=true ;;
  n|N|no|NO|false|FALSE|0) AUTO_UPDATE=false ;;
  *) die "SUPERSTATUS_AUTO_UPDATE must be true or false." ;;
esac

VERSION="${SUPERSTATUS_VERSION:-latest}"
DIR="${SUPERSTATUS_DIR:-./superstatus}"
WEB_PORT="${WEB_PORT:-8080}"
IDENTITY_PORT="${IDENTITY_PORT:-8081}"
BIND_ADDR="${BIND_ADDR:-0.0.0.0}"
SEED_SAMPLE_DATA="${SEED_SAMPLE_DATA:-true}"
WATCHTOWER_SCHEDULE="${WATCHTOWER_SCHEDULE:-0 0 3 * * *}"

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
      SUPERSTATUS_AUTOUPDATE: ${SUPERSTATUS_AUTOUPDATE:-}
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

# Optional Watchtower overlay. It is always written so operators can enable it
# later without re-downloading the installer, but it is only used when
# --auto-update (or SUPERSTATUS_AUTO_UPDATE=true) is set.
cat > docker-compose.watchtower.yml <<'WATCHTOWER_COMPOSE_EOF'
name: superstatus

# Optional self-host auto-update overlay.
#
# Watchtower mounts /var/run/docker.sock, which is root-equivalent access to the
# Docker host. Keep this overlay opt-in and do not use it for org-managed
# staging/production deploys.
#
# Issue #311: Watchtower also runs its http-api so the console's "Update now"
# button can trigger an immediate pull+restart. The api calls
# http://watchtower:8080/v1/update with the shared token (SUPERSTATUS_UPDATE_TOKEN);
# only Watchtower touches the Docker socket. The port is not published to the host.

services:
  api:
    environment:
      SUPERSTATUS_AUTOUPDATE: watchtower
      SUPERSTATUS_UPDATE_TRIGGER_URL: http://watchtower:8080/v1/update
      SUPERSTATUS_UPDATE_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?set SUPERSTATUS_UPDATE_TOKEN in .env (run install.sh --auto-update)}

  watchtower:
    image: containrrr/watchtower:latest
    restart: unless-stopped
    environment:
      WATCHTOWER_CLEANUP: "true"
      WATCHTOWER_LABEL_ENABLE: "true"
      WATCHTOWER_NO_STARTUP_MESSAGE: "true"
      WATCHTOWER_SCHEDULE: "${WATCHTOWER_SCHEDULE:-0 0 3 * * *}"
      WATCHTOWER_SCOPE: "superstatus"
      WATCHTOWER_HTTP_API_UPDATE: "true"
      WATCHTOWER_HTTP_API_TOKEN: ${SUPERSTATUS_UPDATE_TOKEN:?set SUPERSTATUS_UPDATE_TOKEN in .env (run install.sh --auto-update)}
      WATCHTOWER_HTTP_API_PERIODIC_POLLS: "true"
      TZ: "${TZ:-UTC}"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
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
SUPERSTATUS_AUTOUPDATE=
WATCHTOWER_SCHEDULE=${WATCHTOWER_SCHEDULE}
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
  if grep -q '^SUPERSTATUS_VERSION=' .env 2>/dev/null; then
    tmp=$(mktemp 2>/dev/null || echo ".env.tmp.$$")
    ( umask 077; sed "s|^SUPERSTATUS_VERSION=.*|SUPERSTATUS_VERSION=${VERSION}|" .env > "$tmp" )
    mv "$tmp" .env
  else
    printf 'SUPERSTATUS_VERSION=%s\n' "$VERSION" >> .env
  fi
  if grep -q '^WATCHTOWER_SCHEDULE=' .env 2>/dev/null; then
    tmp=$(mktemp 2>/dev/null || echo ".env.tmp.$$")
    ( umask 077; sed "s|^WATCHTOWER_SCHEDULE=.*|WATCHTOWER_SCHEDULE=${WATCHTOWER_SCHEDULE}|" .env > "$tmp" )
    mv "$tmp" .env
  else
    printf 'WATCHTOWER_SCHEDULE=%s\n' "$WATCHTOWER_SCHEDULE" >> .env
  fi
  # Issue #311: ensure a Watchtower http-api token exists for the console's
  # "Update now" button. Generated once and left stable on re-run — never
  # regenerated, so the api ↔ Watchtower shared secret keeps matching.
  if ! grep -q '^SUPERSTATUS_UPDATE_TOKEN=' .env 2>/dev/null; then
    ( umask 077; printf 'SUPERSTATUS_UPDATE_TOKEN=%s\n' "$(gen_secret)" >> .env )
  fi
  chmod 600 .env 2>/dev/null || true
fi

if [ -z "$AUTO_UPDATE" ]; then
  if grep -q '^SUPERSTATUS_AUTOUPDATE=watchtower' .env 2>/dev/null; then
    AUTO_UPDATE=true
  else
    AUTO_UPDATE=false
  fi
fi

if [ "$AUTO_UPDATE" = "true" ]; then
  if grep -q '^SUPERSTATUS_AUTOUPDATE=' .env 2>/dev/null; then
    tmp=$(mktemp 2>/dev/null || echo ".env.tmp.$$")
    ( umask 077; sed 's|^SUPERSTATUS_AUTOUPDATE=.*|SUPERSTATUS_AUTOUPDATE=watchtower|' .env > "$tmp" )
    mv "$tmp" .env
  else
    printf 'SUPERSTATUS_AUTOUPDATE=watchtower\n' >> .env
  fi
else
  if grep -q '^SUPERSTATUS_AUTOUPDATE=' .env 2>/dev/null; then
    tmp=$(mktemp 2>/dev/null || echo ".env.tmp.$$")
    ( umask 077; sed 's|^SUPERSTATUS_AUTOUPDATE=.*|SUPERSTATUS_AUTOUPDATE=|' .env > "$tmp" )
    mv "$tmp" .env
  else
    printf 'SUPERSTATUS_AUTOUPDATE=\n' >> .env
  fi
fi
chmod 600 .env 2>/dev/null || true

# ── 4. Pull + up ──────────────────────────────────────────────────────────────
COMPOSE_FILES="-f docker-compose.yml"
if [ "$AUTO_UPDATE" = "true" ]; then
  COMPOSE_FILES="$COMPOSE_FILES -f docker-compose.watchtower.yml"
fi

say "Pulling images ..."
$DOCKER compose $COMPOSE_FILES pull
say "Starting SuperStatus (first run initializes the database) ..."
$DOCKER compose $COMPOSE_FILES up -d

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
Update:  ${DOCKER} compose pull && ${DOCKER} compose up -d
Auto-update: $([ "$AUTO_UPDATE" = "true" ] && printf 'on (Watchtower, %s)' "$WATCHTOWER_SCHEDULE" || printf 'off')
Stop:    ${DOCKER} compose down        (add -v to also wipe the database)

Serving plain HTTP for a trusted LAN. To expose SuperStatus on the internet,
front it with a TLS reverse proxy — see docs/self-hosting.md.
EOF
