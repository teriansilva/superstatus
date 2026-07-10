#!/usr/bin/env bash
#
# Issue #377 — confine the public demo's outbound traffic.
#
# The demo hands every internet visitor an operator login. An operator can create a
# status check against any URL, and the API will fetch it and report the result — so
# without this guard the demo is an SSRF proxy into the 192.168.15.0/24 homelab and
# into cloud metadata endpoints.
#
# We install a dedicated chain jumped-to from DOCKER-USER, matching on the demo
# bridge (ss-demo0, pinned in docker-compose.demo.yml). Only traffic *originating in*
# the demo network is filtered; inbound to the published ports 8195/8196 arrives on
# the host interface and is untouched.
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
# Requires root (systemd runs it as root; run under sudo by hand).

set -euo pipefail

readonly BRIDGE="ss-demo0"
readonly CHAIN="SS-DEMO-EGRESS"
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

# (1) Intra-demo traffic. Must come first: the demo subnet (10.77.240.0/24) is itself
#     inside one of the ranges rejected below.
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

log "egress guard active on ${BRIDGE}: private ranges rejected; ${PROXY}:443 + DNS allowed"
