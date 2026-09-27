#!/usr/bin/env bash
#
# Issue #377 — rebuild the public demo instance from scratch.
#
# Driven by the superstatus-demo-reset.timer systemd unit at the top of every hour.
# The UI countdown is derived from the same wall clock (DemoMode.NextResetUtc), so the
# timer MUST stay on OnCalendar=hourly or the two will disagree.
#
# Order matters. Everything that can fail is done BEFORE the destructive step, so a
# broken release, an unreachable GHCR, or an invalid compose file leaves the current
# demo up and serving with its data intact — the reset simply doesn't happen this hour:
#
#   guards → git fetch/reset → compose config → compose pull → [down -v] → up -d
#            → egress guard → health poll
#
# `docker compose down -v` destroys pgdata + both DataProtection keyrings for the
# superstatus-demo project. On the same host live superstatus-prod and
# superstatus-staging, whose volumes must never be touched. Three guards stand
# between this script and that mistake, and the project name is a readonly literal
# that is never taken from an argument or the environment.
#
# Run as the user that owns /opt/superstatus-demo — NOT as root. The `git fetch` below
# needs that user's stored Forgejo credentials (~/.git-credentials); under root, HOME is
# /root and the fetch fails with "could not read Username". The only step needing
# elevation is the iptables egress guard, invoked via passwordless sudo.

set -euo pipefail

readonly PROJECT="superstatus-demo"
readonly COMPOSE_FILE="docker-compose.demo.yml"
readonly ENV_FILE=".env.demo"
readonly EXPECTED_DIR="/opt/superstatus-demo"
readonly WEB_HEALTH="http://localhost:8195/health"
readonly IDENTITY_HEALTH="http://localhost:8196/health"
readonly HEALTH_ATTEMPTS=60
readonly HEALTH_INTERVAL=3

log()  { printf '[demo-reset] %s\n' "$*"; }
die()  { printf '[demo-reset] FATAL: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Guards. All three must hold before anything destructive runs.
# ---------------------------------------------------------------------------

# (1) We are in the demo's own checkout — never the /opt/superstatus tree that the
#     prod + staging deploy workflows drive with their own detached checkouts.
cd "${EXPECTED_DIR}" 2>/dev/null || die "cannot cd to ${EXPECTED_DIR}"
[[ "$(pwd -P)" == "${EXPECTED_DIR}" ]] || die "refusing to run outside ${EXPECTED_DIR} (pwd=$(pwd -P))"

# (2) The compose file is the demo one, and it really is the demo (PUBLIC_DEMO is
#     hardcoded there, not read from the env file, so it can't be flipped on a host).
[[ -f "${COMPOSE_FILE}" ]] || die "${COMPOSE_FILE} not found in ${EXPECTED_DIR}"
grep -q '^name: superstatus-demo$' "${COMPOSE_FILE}" \
  || die "${COMPOSE_FILE} does not declare 'name: superstatus-demo' — refusing to touch volumes"
grep -q 'PUBLIC_DEMO: "true"' "${COMPOSE_FILE}" \
  || die "${COMPOSE_FILE} does not enable PUBLIC_DEMO — this is not the demo stack, refusing"

# (3) The untracked env file exists. It is gitignored precisely so the `git reset
#     --hard` below cannot replace it with the CHANGE_ME template.
[[ -f "${ENV_FILE}" ]] || die "${ENV_FILE} missing — copy .env.demo.example and fill it in"

readonly COMPOSE=(docker compose -f "${COMPOSE_FILE}" --env-file "${ENV_FILE}" -p "${PROJECT}")

# (4) We can reach the private repo. Checked up front rather than discovered halfway
#     through: a credential problem here used to surface as a bare "could not read
#     Username" from `git fetch`, which is opaque when it fires from a systemd timer.
git ls-remote --exit-code origin HEAD >/dev/null 2>&1 \
  || die "cannot reach origin as $(id -un) — is ~/.git-credentials present for this user? (the unit must NOT run as root)"

log "guards passed; resetting project ${PROJECT} in ${EXPECTED_DIR}"

# ---------------------------------------------------------------------------
# Fallible work — all of it before the destructive step.
# ---------------------------------------------------------------------------

log "fetching latest main"
git fetch origin --prune --quiet
git reset --hard origin/main --quiet
log "checkout now at $(git rev-parse --short HEAD)"

log "validating compose file"
"${COMPOSE[@]}" config --quiet || die "compose config invalid at $(git rev-parse --short HEAD) — demo left running"

# Pull the latest RELEASED images. If a tag is missing or GHCR is unreachable this
# exits non-zero and the running demo is untouched. This is the whole reason the
# pull precedes `down -v`.
log "pulling images"
"${COMPOSE[@]}" pull --quiet || die "image pull failed — demo left running with existing data"

# ---------------------------------------------------------------------------
# Destructive step. Past this line the demo's data is gone by design.
# ---------------------------------------------------------------------------

log "tearing down ${PROJECT} and destroying its volumes"
"${COMPOSE[@]}" down --volumes --remove-orphans

log "starting ${PROJECT}"
"${COMPOSE[@]}" up -d

# The hourly `down -v` destroys the ss-demo0 bridge, so the iptables rules that
# reference it die with it. Reinstall before the API can run its first status check.
# sudo, not root-for-everything: this is the only step that needs elevation. Invoked by
# ABSOLUTE path so a least-privilege sudoers rule can whitelist exactly this command:
#   marcusbraun ALL=(root) NOPASSWD: /opt/superstatus-demo/scripts/demo-egress-guard.sh
log "reinstalling egress guard"
sudo -n "${EXPECTED_DIR}/scripts/demo-egress-guard.sh"

# ---------------------------------------------------------------------------
# Verify.
# ---------------------------------------------------------------------------

wait_healthy() {
  local url="$1" name="$2" i
  for ((i = 1; i <= HEALTH_ATTEMPTS; i++)); do
    if curl -fsS --max-time 5 "${url}" >/dev/null 2>&1; then
      log "${name} healthy after $((i * HEALTH_INTERVAL))s"
      return 0
    fi
    sleep "${HEALTH_INTERVAL}"
  done
  return 1
}

if ! wait_healthy "${IDENTITY_HEALTH}" identity || ! wait_healthy "${WEB_HEALTH}" web; then
  log "health check failed; dumping state"
  "${COMPOSE[@]}" ps || true
  "${COMPOSE[@]}" logs --tail=200 web api identity postgres || true
  die "demo did not come back healthy"
fi

log "reset complete at $(date -u +%FT%TZ); next reset at the top of the hour"
