#!/bin/sh
#
# SuperStatus uninstaller — the counterpart to install.sh.
#
#   curl -fsSL https://superstatus.superstatus.io/uninstall.sh | sh
#   # …or, from your install directory:
#   sh uninstall.sh
#
# Removes the SuperStatus stack this host is running: the containers, the
# database volume (pgdata — ALL status history and accounts), and the private
# compose network. By default it also removes the pulled images and the install
# directory install.sh created (docker-compose.yml, .env, postgres/init script).
#
# It does NOT uninstall Docker itself.
#
# Because this deletes the database, it asks for an explicit "yes" first. Piped
# (curl … | sh) it still prompts on the terminal; in a non-interactive context
# set SUPERSTATUS_YES=1 to proceed.
#
# Environment overrides (all optional):
#   SUPERSTATUS_DIR        install directory      (default ./superstatus, or .
#                          if the current directory holds the stack)
#   SUPERSTATUS_YES=1      skip the confirmation prompt (required to run
#                          non-interactively — it deletes the database)
#   SUPERSTATUS_KEEP_IMAGES=1   keep the pulled images
#   SUPERSTATUS_KEEP_DIR=1      keep the install directory and its files
set -eu

# Compose project name — set by the `name:` field in the generated
# docker-compose.yml, so the volume/network are superstatus_pgdata /
# superstatus_internal and every resource carries the project label.
PROJECT=superstatus

say() { printf '%s\n' "$*"; }
err() { printf 'error: %s\n' "$*" >&2; }
die() { err "$*"; exit 1; }

# ── Docker reachability (mirror install.sh: fall back to sudo if needed) ───────
command -v docker >/dev/null 2>&1 \
  || die "Docker not found — nothing to uninstall (or Docker was already removed)."

DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
  if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
    DOCKER="sudo docker"
  else
    die "Cannot talk to the Docker daemon. Is it running, and can this user reach it (docker group / sudo)?"
  fi
fi

# ── Locate the install directory ──────────────────────────────────────────────
# Explicit override wins; otherwise use the current directory if it holds this
# stack (a docker-compose.yml named `superstatus`), else the install default.
DIR="${SUPERSTATUS_DIR:-}"
if [ -z "$DIR" ]; then
  if [ -f docker-compose.yml ] && grep -q '^name: superstatus$' docker-compose.yml 2>/dev/null; then
    DIR="."
  else
    DIR="./superstatus"
  fi
fi
HAVE_COMPOSE=no
[ -f "$DIR/docker-compose.yml" ] && HAVE_COMPOSE=yes

# Is there anything from this project on the host at all?
EXISTING=$($DOCKER ps -aq --filter "label=com.docker.compose.project=${PROJECT}" 2>/dev/null | wc -l | tr -d ' ')
if [ "$HAVE_COMPOSE" = no ] && [ "${EXISTING:-0}" = 0 ] \
   && ! $DOCKER volume inspect "${PROJECT}_pgdata" >/dev/null 2>&1; then
  die "No SuperStatus install found (no compose file in '$DIR', no '${PROJECT}' containers, no '${PROJECT}_pgdata' volume). If you used a custom directory, pass SUPERSTATUS_DIR=…"
fi

# ── Confirm (this deletes the database) ───────────────────────────────────────
say "This will permanently remove the SuperStatus stack:"
say "  • containers            (postgres, identity, api, web)"
say "  • the database volume    ${PROJECT}_pgdata  — ALL status history & accounts"
say "  • the compose network    ${PROJECT}_internal"
[ "${SUPERSTATUS_KEEP_IMAGES:-}" = 1 ] \
  || say "  • the pulled images      (superstatus-{web,api,identity}, postgres:16-alpine)"
if [ "$HAVE_COMPOSE" = yes ] && [ "${SUPERSTATUS_KEEP_DIR:-}" != 1 ]; then
  say "  • install files in       $DIR  (docker-compose.yml, .env, postgres/init-databases.sh)"
fi
say "Docker itself is left installed."
say ""

if [ "${SUPERSTATUS_YES:-}" != 1 ]; then
  if [ -r /dev/tty ]; then
    printf 'Type "yes" to proceed: ' > /dev/tty
    read -r _ans < /dev/tty 2>/dev/null || _ans=""
    [ "$_ans" = yes ] || die "Aborted — nothing was removed."
  else
    die "Refusing to uninstall without confirmation. Re-run with SUPERSTATUS_YES=1 (this deletes the database)."
  fi
fi

# ── 1. Compose down -v: containers + named volume + network (preferred path) ──
if [ "$HAVE_COMPOSE" = yes ]; then
  say "Stopping and removing the stack ..."
  ( cd "$DIR" && $DOCKER compose down -v --remove-orphans ) || true
fi

# ── 2. Belt-and-suspenders cleanup by project label + explicit names ──────────
# Catches a stack whose compose file was already deleted, or a partial install.
say "Cleaning up any remaining project resources ..."
$DOCKER ps -aq --filter "label=com.docker.compose.project=${PROJECT}" \
  | xargs -r $DOCKER rm -f >/dev/null 2>&1 || true
$DOCKER volume ls -q --filter "label=com.docker.compose.project=${PROJECT}" \
  | xargs -r $DOCKER volume rm >/dev/null 2>&1 || true
$DOCKER volume rm "${PROJECT}_pgdata" >/dev/null 2>&1 || true
$DOCKER network rm "${PROJECT}_internal" >/dev/null 2>&1 || true

# ── 3. Images (unless kept) ───────────────────────────────────────────────────
if [ "${SUPERSTATUS_KEEP_IMAGES:-}" != 1 ]; then
  say "Removing images ..."
  for img in \
    ghcr.io/teriansilva/superstatus-web \
    ghcr.io/teriansilva/superstatus-api \
    ghcr.io/teriansilva/superstatus-identity
  do
    $DOCKER images -q "$img" | sort -u | xargs -r $DOCKER rmi -f >/dev/null 2>&1 || true
  done
  # postgres:16-alpine is a shared upstream image — only remove it if nothing
  # else on the host is using it.
  if [ -z "$($DOCKER ps -aq --filter ancestor=postgres:16-alpine 2>/dev/null)" ]; then
    $DOCKER rmi postgres:16-alpine >/dev/null 2>&1 || true
  fi
fi

# ── 4. Install files (unless kept) ────────────────────────────────────────────
# Only the files install.sh writes — never a stray backup or anything else in
# the directory. The directory itself is removed only when it's not the cwd.
if [ "$HAVE_COMPOSE" = yes ] && [ "${SUPERSTATUS_KEEP_DIR:-}" != 1 ]; then
  say "Removing install files in $DIR ..."
  rm -f "$DIR/docker-compose.yml" "$DIR/.env" "$DIR/postgres/init-databases.sh" 2>/dev/null || true
  rmdir "$DIR/postgres" 2>/dev/null || true
  if [ "$DIR" != "." ]; then
    rmdir "$DIR" 2>/dev/null \
      || say "  (kept $DIR — it still contains other files, e.g. a backup)"
  fi
fi

say ""
say "SuperStatus has been removed."
say "Docker Engine was left installed — to remove it too see"
say "https://docs.docker.com/engine/install/ (the 'Uninstall' section for your OS)."
