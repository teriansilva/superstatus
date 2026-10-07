#!/usr/bin/env bash
#
# Issue #377 — confine the public demo's outbound traffic.
#
# The demo hands every internet visitor an operator login. An operator can create a
# status check against any URL, and the API will fetch it and report the result — so
# without this guard the demo is an SSRF proxy into the 192.168.15.0/24 homelab and
# into cloud metadata endpoints.
#
# Two chains are needed, because a container reaches a target by two different kernel
# paths:
#
#   * Container -> a REMOTE address (the internet, or the load balancer 192.168.15.5)
#     is *forwarded* by the host, so it traverses the FORWARD chain and our
#     DOCKER-USER jump. This is the SS-DEMO-EGRESS chain below.
#
#   * Container -> the DOCKER HOST'S OWN IP (192.168.15.10) on a published port is NOT
#     forwarded — it is delivered locally and accepted by `docker-proxy`, so it hits
#     the INPUT chain and NEVER passes through DOCKER-USER. Without a second chain the
#     demo could reach 192.168.15.10:8190 / :8192 — i.e. the PRODUCTION and STAGING web
#     apps published on this same host. This was observed (HTTP 200) before the
#     SS-DEMO-INPUT chain was added. The demo has no legitimate need to talk to the host
#     itself (its DNS is docker's embedded resolver, host-originated; its OIDC
#     back-channel goes to the LB, not the host), so INPUT from the demo bridge is
#     rejected outright apart from replies to connections it already established.
#
# Only traffic *originating in* the demo network is filtered; inbound to the published
# ports 8195/8196 arrives on the host's own interface, not ss-demo0, and is untouched.
#
# The block is deny-by-default over the private ranges, with three exceptions that
# the stack genuinely needs:
#
#   1. demo → demo (postgres, api, identity over the compose network).
#   2. demo → 192.168.15.5:443, the reverse proxy. Web's OIDC back-channel resolves
#      id.demo.status.superstatus.io to the LB via split-horizon DNS, exactly as
#      staging does. Without this, login dies at the code→token exchange.
#   3. demo → the host resolvers on port 53, or nothing resolves at all.
#
# Everything else in 10/8, 172.16/12, 192.168/16, 169.254/16 (link-local + cloud
# metadata) and 127/8 is rejected. Public internet egress is untouched, so real
# status checks against real sites still work.
#
# Idempotent: flushes and rebuilds the chain on every run. Called by demo-reset.sh
# after `up -d`, because the hourly `down -v` destroys and recreates the bridge.
#
# This script itself must run as root (it writes iptables rules). It is NOT run by a
# root systemd unit — superstatus-demo-reset.service runs as the checkout owner, and
# demo-reset.sh elevates only this one command through a scoped sudoers rule:
#   marcusbraun ALL=(root) NOPASSWD: /opt/superstatus-demo/scripts/demo-egress-guard.sh
# To run it by hand: sudo /opt/superstatus-demo/scripts/demo-egress-guard.sh

set -euo pipefail

readonly BRIDGE="ss-demo0"
readonly CHAIN="SS-DEMO-EGRESS"
readonly INPUT_CHAIN="SS-DEMO-INPUT"
readonly PROXY="192.168.15.5"
readonly RESOLVERS=("192.168.15.10" "192.168.15.1")
readonly PRIVATE_RANGES=("10.0.0.0/8" "172.16.0.0/12" "192.168.0.0/16" "169.254.0.0/16" "127.0.0.0/8")

log() { printf '[demo-egress-guard] %s\n' "$*"; }

if [[ ${EUID} -ne 0 ]]; then
  echo "[demo-egress-guard] must run as root" >&2
  exit 1
fi

if ! ip link show "${BRIDGE}" >/dev/null 2>&1; then
  echo "[demo-egress-guard] bridge ${BRIDGE} does not exist — is superstatus-demo up?" >&2
  exit 1
fi

# Rebuild the chain from scratch so repeated runs never stack duplicate rules.
iptables -N "${CHAIN}" 2>/dev/null || true
iptables -F "${CHAIN}"

# (0) Replies to already-established flows. This MUST be first, and it is load-bearing:
#     an inbound request proxied by the load balancer (LB -> host:8195 -> demo container)
#     is answered by the container with a packet to 192.168.15.5 on the LB's EPHEMERAL
#     port, not 443. Without this line that reply matches neither the :443 allow below
#     nor intra-demo, and gets caught by the 192.168/16 REJECT — silently breaking every
#     inbound request through the proxy (observed: external requests timed out while the
#     app was healthy on localhost). It does NOT weaken egress: a demo-INITIATED
#     connection to an internal target is state NEW, so it still falls through to the
#     REJECT rules; only return traffic of a permitted/inbound flow is let back.
iptables -A "${CHAIN}" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN

# (1) Intra-demo traffic. The demo subnet (10.77.240.0/24) is itself inside one of the
#     ranges rejected below, so this must precede them.
iptables -A "${CHAIN}" -i "${BRIDGE}" -o "${BRIDGE}" -j RETURN

# (2) The reverse proxy, for the OIDC back-channel only.
iptables -A "${CHAIN}" -i "${BRIDGE}" -d "${PROXY}" -p tcp --dport 443 -j RETURN

# (3) DNS to the host resolvers.
for resolver in "${RESOLVERS[@]}"; do
  iptables -A "${CHAIN}" -i "${BRIDGE}" -d "${resolver}" -p udp --dport 53 -j RETURN
  iptables -A "${CHAIN}" -i "${BRIDGE}" -d "${resolver}" -p tcp --dport 53 -j RETURN
done

# (4) Deny the rest of the private address space. REJECT rather than DROP so a demo
#     visitor's check fails fast with a clear error instead of hanging until timeout.
for range in "${PRIVATE_RANGES[@]}"; do
  iptables -A "${CHAIN}" -i "${BRIDGE}" -d "${range}" \
    -j REJECT --reject-with icmp-admin-prohibited
done

iptables -A "${CHAIN}" -j RETURN

# Jump into the chain from DOCKER-USER, exactly once, at the top.
if ! iptables -C DOCKER-USER -i "${BRIDGE}" -j "${CHAIN}" 2>/dev/null; then
  iptables -I DOCKER-USER 1 -i "${BRIDGE}" -j "${CHAIN}"
  log "installed DOCKER-USER jump for ${BRIDGE}"
fi

# ---------------------------------------------------------------------------
# INPUT chain — demo -> the docker host itself. Closes the docker-proxy hole.
# ---------------------------------------------------------------------------
iptables -N "${INPUT_CHAIN}" 2>/dev/null || true
iptables -F "${INPUT_CHAIN}"

# Let replies to connections the demo legitimately established flow back.
iptables -A "${INPUT_CHAIN}" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN

# Everything else demo->host is rejected: the demo must not reach the host's published
# ports (prod 8190/8191, staging 8192/8193, landing 8194), the docker API, or any other
# host-local service. REJECT (not DROP) for a fast, clear failure.
iptables -A "${INPUT_CHAIN}" -j REJECT --reject-with icmp-admin-prohibited

if ! iptables -C INPUT -i "${BRIDGE}" -j "${INPUT_CHAIN}" 2>/dev/null; then
  iptables -I INPUT 1 -i "${BRIDGE}" -j "${INPUT_CHAIN}"
  log "installed INPUT jump for ${BRIDGE}"
fi

log "egress guard active on ${BRIDGE}: private ranges + host-local rejected; ${PROXY}:443 + DNS allowed"
