# Installer verification check (`verify-install.yml`)

The one-line installer is the **primary onboarding path** for self-hosters:

```bash
curl -fsSL https://superstatus.superstatus.io/install.sh | sh
# …and its counterpart
curl -fsSL https://superstatus.superstatus.io/uninstall.sh | sh
```

Nothing in the normal build/test gate exercises these scripts, so a regression
in `install.sh`, `uninstall.sh`, or the compose/`.env`/init templates they emit
would ship silently — the first signal being a frustrated operator. The
`.forgejo/workflows/verify-install.yml` check closes that gap: it runs **both
scripts end-to-end** against a pristine, disposable environment and asserts the
stack comes up healthy and then tears down to nothing.

## Two legs

The check is a matrix with two legs (`fail-fast: false`, so both always report):

| Leg | Environment | Exercises |
|---|---|---|
| `dind` | A `docker:<ver>-dind` container that **already has Docker**. | The installer with `SUPERSTATUS_INSTALL_DOCKER=no` — the fast path against a machine that already has Docker + Compose. |
| `os-autoinstall` | A **plain Ubuntu (no Docker) systemd container**. | `SUPERSTATUS_INSTALL_DOCKER=yes` → `install.sh` installs Docker via **`https://get.docker.com`** + `systemctl enable --now docker` — the branch a fresh self-hoster on a bare VM actually hits (#332). |

Both legs then share the same assertions: `/health` returns 200, the volume /
network / files exist, `uninstall.sh` runs, and everything is gone.

## What each leg does

**Common to both** (all steps drive the environment from outside via `docker exec`):

1. Confirm the published `:edge` app images exist (skip-with-warning if not).
2. Copy the PR's `install.sh` / `uninstall.sh` into the throwaway container.
3. Run `install.sh` non-interactively (`SUPERSTATUS_VERSION=edge`,
   `SEED_SAMPLE_DATA=false`, `BIND_ADDR=0.0.0.0`, `HOST_IP=localhost`).
4. **Verify installed:** compose stack up, `http://127.0.0.1:8080/health` → 200,
   the `superstatus_pgdata` volume + `superstatus_internal` network exist, and the
   generated files are present.
5. Run `SUPERSTATUS_YES=1 sh uninstall.sh`.
6. **Verify removed:** no `superstatus` containers, no `superstatus_pgdata`
   volume, no `superstatus_internal` network, install dir gone.
7. `always()` teardown: `docker rm -fv` the container (dropping its anonymous
   `/var/lib/docker`[`,containerd`] volumes) and, for `os-autoinstall`, the
   locally-built systemd image — **nothing is left on the runner host.**

**`dind` leg only:** start a privileged `docker:28-dind`, wait for the inner
daemon, then run `install.sh` with `SUPERSTATUS_INSTALL_DOCKER=no`.

**`os-autoinstall` leg only:** build a minimal systemd image **from the official
`ubuntu:24.04`** (systemd + curl + a few basics — no third-party image), boot it,
wait for `systemctl is-system-running` (`running`/`degraded`), and **assert no
`docker` binary exists**. Then run `install.sh` with
`SUPERSTATUS_INSTALL_DOCKER=yes` — which pulls + runs `get.docker.com` — and
afterwards **assert Docker was installed by the script** (`docker version`,
`systemctl is-active docker`).

### Why containers on the runner (and why one needs systemd)

The Forgejo runner is registered `self-hosted:host`, so jobs run **directly on
`mb-infrabot`**, sharing its Docker daemon and filesystem with live containers.
Running `install.sh` there would be neither pristine nor safe — it would collide
with real workloads and litter the host. So each leg runs in its own throwaway
privileged container; teardown is a single `docker rm -fv`.

The `os-autoinstall` leg needs **systemd as PID 1** because `install.sh`'s
`install_docker()` runs `get.docker.com` and then `systemctl enable --now
docker`; its very next step is a `docker info` reachability check that hard-fails
if the daemon isn't running. A plain container without an init can't start the
daemon that way, so the leg boots a systemd container (`--privileged
--cgroupns=host` with `/sys/fs/cgroup` mounted).

> **Host-mount note:** the only thing bind-mounted from the host is the
> `os-autoinstall` leg's `/sys/fs/cgroup` (read-write) — the standard requirement
> for systemd-in-a-container. It is **not** a host data mount and **not** the
> Docker socket. Everything the tests create lives inside the throwaway
> container's namespaces.

> **Two volumes are load-bearing (`os-autoinstall`):** `get.docker.com` installs a
> *system* containerd whose overlay snapshotter lives at `/var/lib/containerd`
> (separate from `/var/lib/docker`). Both are given ext4-backed anonymous volumes;
> without that the nested daemon stacks **overlay-on-overlay** and container
> create fails with `invalid argument`. (The `dind` leg only needs
> `/var/lib/docker`, which its base image already volumes.)

### Why `:edge` and not per-PR images

The check validates **the script** and the templates it writes — not unbuilt
per-PR application images. `:edge` is the published-from-`main` image set
(`release-images.yml`), so the installer exercises exactly what a self-hoster
would pull. Testing PR-built images is explicitly out of scope for this check.

## When it runs

| Trigger | Purpose |
|---|---|
| `pull_request` (path-filtered) | On PRs touching `install.sh`, `uninstall.sh`, `docker-compose*.yml`, `postgres/init-databases.sh`, or the workflow itself. Most PRs don't touch these and pay nothing. |
| `schedule` (nightly, 03:27 UTC) | Catches drift in the published `:edge` images / `get.docker.com` even when the scripts didn't change. |
| `workflow_dispatch` | Manual re-run. |

### Is it a "required" check?

On an installer-touching PR each leg publishes a commit status that becomes part
of the **combined commit status** the agent-workflow merge gate already enforces
(`docs/agent-workflow.md` §7/§8) — so both legs are effectively required for
exactly the PRs that change the installer, and absent (correctly) on PRs that
don't.

This repo has **no Forgejo branch protection**; gating is the combined-status +
Hermes model. A global branch-protection required-check is deliberately *not*
used here: Forgejo can't scope a required check to a path, so a global one would
block every unrelated PR that never triggers this workflow.

## Reproduce locally

You need Docker on your machine.

### `dind` leg (Docker already present)

```bash
DIND=ss-verify-local
docker run -d --privileged --name "$DIND" -e DOCKER_TLS_CERTDIR="" docker:28-dind
until docker exec "$DIND" docker info >/dev/null 2>&1; do sleep 1; done
docker exec "$DIND" mkdir -p /work
docker cp install.sh   "$DIND:/work/install.sh"
docker cp uninstall.sh "$DIND:/work/uninstall.sh"
docker exec -e SUPERSTATUS_VERSION=edge -e SEED_SAMPLE_DATA=false \
  -e SUPERSTATUS_INSTALL_DOCKER=no -e SUPERSTATUS_DIR=/work/superstatus \
  -e BIND_ADDR=0.0.0.0 -e HOST_IP=localhost \
  "$DIND" sh -c 'cd /work && sh install.sh'
# Probe 127.0.0.1, not localhost — busybox resolves localhost to ::1 but
# docker-proxy publishes on IPv4.
docker exec "$DIND" wget -q -O- http://127.0.0.1:8080/health && echo OK
docker exec -e SUPERSTATUS_YES=1 -e SUPERSTATUS_DIR=/work/superstatus \
  "$DIND" sh -c 'cd /work && sh uninstall.sh'
docker rm -fv "$DIND"      # also drops the anonymous volume
```

### `os-autoinstall` leg (get.docker.com on a plain OS)

```bash
# A systemd image from the official ubuntu:24.04 (curl baked in — the real
# entrypoint is `curl … | sh`).
docker build -t ss-systemd - <<'EOF'
FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends \
      systemd systemd-sysv curl ca-certificates sudo iproute2 && rm -rf /var/lib/apt/lists/*
CMD ["/sbin/init"]
EOF

C=ss-verify-os
# Ext4-backed volumes at BOTH /var/lib/docker and /var/lib/containerd (see the
# load-bearing note above). --privileged --cgroupns=host to run systemd.
docker run -d --name "$C" --privileged --cgroupns=host \
  -v /sys/fs/cgroup:/sys/fs/cgroup:rw --tmpfs /run --tmpfs /tmp \
  -v /var/lib/docker -v /var/lib/containerd ss-systemd
until docker exec "$C" systemctl is-system-running 2>/dev/null | grep -qE 'running|degraded'; do sleep 1; done

docker exec "$C" mkdir -p /work
docker cp install.sh   "$C:/work/install.sh"
docker cp uninstall.sh "$C:/work/uninstall.sh"
# SUPERSTATUS_INSTALL_DOCKER=yes -> install.sh runs get.docker.com itself.
docker exec -e SUPERSTATUS_INSTALL_DOCKER=yes -e SUPERSTATUS_VERSION=edge \
  -e SEED_SAMPLE_DATA=false -e SUPERSTATUS_DIR=/work/superstatus \
  -e BIND_ADDR=0.0.0.0 -e HOST_IP=localhost \
  "$C" sh -c 'cd /work && sh install.sh'
docker exec "$C" docker version               # installed by the script
docker exec "$C" curl -fsS http://127.0.0.1:8080/health && echo OK
docker exec -e SUPERSTATUS_YES=1 -e SUPERSTATUS_DIR=/work/superstatus \
  "$C" sh -c 'cd /work && sh uninstall.sh'
docker rm -fv "$C" && docker rmi ss-systemd    # no residue
```

A green run ends with the stack removed and no `superstatus_*` resources left.
To see the check do its job, break something in `install.sh` (e.g. a bad image
name) and re-run the install step — the leg fails.
