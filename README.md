# SuperStatus

**A status page you host yourself.** Point it at the services you care about,
and it watches them around the clock, shows your users a clean uptime page, and
lets you post incident updates when something breaks.

Free to self-host, source-available under the BSL 1.1. One command to run.

---

## Why SuperStatus

- **Self-hosted** — your data stays on your machine. No accounts, no SaaS, no
  per-monitor pricing.
- **One command** — `docker compose up` and you have a working status page.
- **Honest defaults** — runs on plain HTTP on your own machine out of the box;
  add a domain and TLS when you're ready.

## What it does

- **Uptime monitoring** — add the URLs you want watched; a background job checks
  each on its own interval and records up/down and response time, backing off
  automatically when something is failing.
- **Public status page** — a clean dashboard (light/dark) your users can check,
  with per-service history and response-time charts, plus an at-a-glance grid.
- **Incidents** — write and update incident reports from the admin UI. Checks can
  optionally open incidents automatically when they fail.
- **Webhooks** — get notified when status changes, with a built-in delivery log.
- **Single admin login** — the first visit walks you through creating your
  administrator account. No default passwords.

## Run it

You need **Docker 20.10+** with **Compose v2**. Nothing else — no database or
runtime to install.

```bash
git clone https://git.superstatus.io/superstatus.io/superstatus.git
cd superstatus
```

### On a server, reached by IP (recommended)

Running it on a box your team reaches over the network? One command:

```bash
./start.sh
```

It detects the server's IP, generates secrets, builds, and starts everything.
When it prints the URL, open **http://&lt;server-ip&gt;:8080** from any machine on
your network and create your admin account. A sample service is already being
monitored so the page isn't empty.

> `start.sh` serves plain HTTP on your LAN — fine for a trusted network. To put
> it on the public internet, use a TLS reverse proxy instead (see below).

### Just trying it on your laptop

```bash
docker compose up
```

Open **http://localhost:8080**. The defaults need no edits.

> Using Safari and the login page doesn't load? Add `127.0.0.1 id.localhost` to
> your hosts file. Most browsers don't need this.

### Putting it on a real domain

Ready to share it with the world? Don't expose the plain-HTTP defaults to the
internet — put it behind a reverse proxy with TLS and change the demo secrets
first. **[docs/self-hosting.md](docs/self-hosting.md)** has a copy-paste recipe,
plus backup/restore and admin-recovery steps.

## Roadmap

Where things are headed (kept short and current):

- ✅ One-command self-hosting (source-available, BSL 1.1)
- 🔜 Logins survive updates (persisted keys/certs)
- 🔜 Prebuilt images so `docker compose up` starts in seconds
- 🔜 Security audit → first public release
- 💡 Optional hosted version (**SuperStatus Cloud**) for people who'd rather not
  run it themselves

## License

[Business Source License 1.1](LICENSE) — **source-available**, not OSI "open
source". You may read, modify, and **self-host it in production for free**,
including for your organization's internal use. The one thing you may not do is
offer it to third parties as a hosted/managed status-page service that competes
with the maintainer's paid offering (**SuperStatus Cloud**).

Each released version automatically converts to **Apache-2.0** two years after
it is published (the BSL "Change Date") — so today's release is fully open source
in two years. Want to run it as a competing service, or embed it without the BSL
terms? A **commercial license** is available — contact licensing@superstatus.io.

## More

- [Self-hosting guide](docs/self-hosting.md) — TLS, backups, recovery
- [Architecture](docs/architecture.md) — how it's built
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)
