# Check providers — architecture & trust boundary

Epic [#271](https://git.superstatus.io/superstatus.io/superstatus/issues/271);
Phase 1 = [#312](https://git.superstatus.io/superstatus.io/superstatus/issues/312).

A **check** used to be hardwired to "`GET url` → assert HTTP status + response
time". That check type is now a **provider** behind a small in-process seam, so new
things to monitor (AI/LLM endpoints, agent heartbeats, TCP/DNS/TLS-cert, …) become
plugins on the one engine rather than bespoke features.

The engine keeps everything it already does — scheduling, history, uptime, rollups,
incidents, and email/push/webhook alerting. A provider answers only two questions:

- **how do I probe this target** (`ProbeAsync`), and
- **what does its config look like** (`Descriptor` → versioned `ConfigSchema`).

## The contract (`SuperStatus.Services/Providers`)

```
ICheckProvider
  Descriptor { TypeId, DisplayName, Icon, ConfigSchema (versioned), MetricDefs,
               Description, Direction (pull|push) }
  Task<ProbeResult> ProbeAsync(ProbeContext ctx, CancellationToken ct)

ProbeResult  { FailType, LatencyMs, Reachable, HttpStatusCode?, MetricsJson?, Message }
ProbeContext { CheckId, CheckTitle, ConfigJson (validated), per-probe Timeout }
```

- `Description` + `Direction` (#335) are operator-facing descriptor metadata for UI
  surfaces (the Plugins page): one sentence on what the provider does, and whether it
  probes out (`pull`: http, ai) or waits for the target to ping in (`push`: heartbeat).
  Server-driven so no UI carries page-local provider prose.

- `ConfigSchema` is a **closed field vocabulary** — `text | number | bool | secret |
  select` — so the edit dialog renders any provider's form generically. There is no
  arbitrary form engine; the vocabulary grows only when a real provider needs it.
- `MetricDefs` are **typed up front**. A provider may only emit metrics it declared.
  **Phase 1 ships zero metrics** — `HistoricalStatusData.MetricsJson` is always null;
  retention/query semantics are defined when Phase 2 lands.

The engine adapts a `ProbeResult` back onto the existing `HistoricalStatusData` fields
(`FailType`, `HttpStatusCode`, `ResponseTimeInMs`, `CheckFailed`), so every downstream
read — status, history, rollups, auto-incident, alerts, the public `/api/status`, and
run-now — is byte-for-byte unchanged. The cross-cutting latency SLO (the linked SLA's
slow threshold → `ResponseTime`/degraded) stays in the engine, not in any provider.

## Trust boundary (decided in Phase 1, before any provider-loading code)

> **In-process providers are trusted, first-party, reviewed code.** They run inside the
> API process with full access — no sandbox. This tier is for code the maintainers
> wrote and reviewed.

> **Untrusted / community code is out of scope until Phase 4.** The only path permitted
> to run code the maintainers did not review is the later **out-of-process** plugin
> protocol (an exec / HTTP runner returning normalized JSON, optionally WASM-sandboxed),
> which must be **time- and resource-bounded and sandboxed**. Untrusted code must
> **never** be loaded in-process.

This boundary is documented **now**, in Phase 1, so that the in-process registry
(`ICheckProviderRegistry`) is never mistaken for a general "plugin loader" that could
safely run untrusted code. The registry only ever holds first-party `ICheckProvider`
implementations wired through DI.

| Tier | Trust | Isolation | Status |
|---|---|---|---|
| In-process C# providers (`ICheckProvider`) | Trusted, first-party, reviewed | None (full process access) | **Phase 1+** |
| Out-of-process runner (exec / HTTP / WASM) | Untrusted / community | Sandboxed, time- & resource-bounded | **Phase 4 (later / experimental)** |

## Safety guarantees (Phase 1)

- **Probe containment.** Every probe runs under a per-probe timeout and a hard
  try/catch (`StatusCheckService.RunProbeSafelyAsync`). A provider that throws or hangs
  is converted to a normalized `down` / `Unreachable` result and **never** propagates
  into the scheduler tick, backoff, auto-incident, or alert pipeline.
- **Config validation & versioning.** Stored `ConfigJson` is validated against the
  provider's current `ConfigSchema` on load (`ConfigSchema.Validate`). Invalid /
  unparseable / incompatible-version config, **and an unknown/missing `ProviderType`**,
  **disable the check and surface the reason calmly** in the console — never a crash,
  never a silent default probe. The exact same gate (`StatusCheckService.ResolveProbe`)
  is consulted by both the scheduled tick and the manual run-now path.
- **Secrets safe by default.** `secret`-typed config fields follow the existing
  SMTP/AI-key rule: write-only on the API boundary, **masked on read** (never echoed),
  **preserved when re-saved blank** (`ProviderConfigWriter`), and never serialized into
  the public status API or the dashboard. The rule lands with the vocabulary in Phase 1,
  before any credential-bearing provider exists, so none can leak a credential by
  default.

## Data model

- `StatusCheck` gains `ProviderType` (default `http`) + `ConfigJson` (provider config).
  Existing HTTP checks migrate to `ProviderType = http` with `ConfigJson` populated from
  the legacy `StatusCheckUrl` / `ExpectedStatusCode` columns, which stay live and
  authoritative for old read consumers in Phase 1 (no column deprecation — later/explicit).
- `HistoricalStatusData` gains a nullable `MetricsJson` (unused in Phase 1; populated by
  metric-emitting providers from Phase 2a).

## Metrics (Phase 2a, #317)

A provider declares typed `MetricDef`s (`Key`, `Label`, `Unit`, `Kind` = gauge|counter,
optional warn/crit thresholds) on its `Descriptor.MetricDefs`. On each tick it may emit a
flat `{ key: number }` object in `ProbeResult.MetricsJson`; the engine **sanitizes it
against the declared defs** (`MetricsValidator`) before persisting — **only declared,
finite, numeric keys survive** (undeclared/non-numeric/`NaN`/`Infinity` are dropped; an
empty result stays `null`, so an HTTP check still persists `null`). There is **no
unbounded blob and no free-form metric**.

**Retention & query (2a).** Metrics live on `HistoricalStatusData.MetricsJson`, so they
share the **raw-tick window only (≈72 h)** and are pruned with it — there is **no metric
rollup or long-term store in 2a** (that's a later, explicit step, like the day rollups).

Read API:

- `GET /statuscheck/providers` — each descriptor now includes `metrics[]` (the declared
  `MetricDef`s) so a consumer knows how to label/threshold.
- `GET /statuscheck/{id}/metrics?count=N` — `{ statusCheckId, providerType, metricDefs[],
  samples[] }` where each `sample` is `{ timeUtc, values: { key: number } }` from the
  recent raw ticks (ascending by time). Dashboard rendering of this is **Phase 2c**.

`MostRecentState` (the dashboard's up/degraded/down) derives from the stored `FailType`
the provider classified — provider-agnostic, identical to pre-#317 for HTTP, and correct
for non-HTTP providers (whose `HttpStatusCode` is always 0).

## AI / LLM canary provider (`ai`, Phase 2a, #317)

An OpenAI-compatible LLM canary. Config (rendered by the schema-driven dialog): `baseUrl`,
`model`, `apiKey` (**secret**), `prompt`, `expectContains`, optional `maxTokens` /
`ttftThresholdMs` / `minTokensPerSec`. It `POST`s `{baseUrl}/chat/completions` with
`stream:true` (and `stream_options.include_usage`), measures **TTFT** at the first content
token, accumulates content, and computes throughput; it tolerates a non-streaming endpoint
by falling back to a plain completion parse. It has a longer per-probe ceiling
(`Descriptor.ProbeTimeout`, 30s) than HTTP.

Outcomes map onto the existing `FailType` so incidents / alerts / `/api/status` / rollups
are unchanged:

| Condition | `FailType` | State |
|---|---|---|
| transport error / timeout / non-2xx | `Unreachable` | down |
| reachable but `expectContains` not found | `StatusCode` | down |
| content OK but TTFT > threshold or tok/s < floor | `ResponseTime` | degraded |
| otherwise | `NoFail` | up |

Metrics emitted: `ttft_ms`, `tokens_per_sec`, `latency_ms`, `completion_tokens`. The API
key is write-only/masked (the Phase-1 `secret` rule) and never reaches logs, the public
API, incidents, or the dashboard; probe errors are logged by **exception type only**.

## Agent-heartbeat provider (`heartbeat`, Phase 2b, #320)

> **Status: parked — not currently registered** (operator decision, 2026-07-07).
> The provider class, its tests, and this doc remain; the DI registration in
> `SuperStatus.ApiService/Services/ServiceRegistration.cs` is commented out, and the
> heartbeat endpoints below are mapped only while the provider is registered (so no
> anonymous ping sink exists while parked). Re-enabling = restoring that one line.
> Existing `heartbeat` checks are disabled calmly by the #312 unknown-type gate and
> surface as "not registered" on the Plugins page.

The first **push / dead-man's-switch** provider: instead of SuperStatus probing outward, an
agent / cron job / worker **pings inward** each run, and the check goes **down** when a ping
is overdue. This is the inverse of `http`/`ai` — there is no outbound call.

- **The generic seam extension is one optional field:** `ProbeContext.LastSignalUtc` — the
  UTC of the last inbound ping the engine recorded for this check. Pull providers
  (`http`/`ai`) receive `null`; only the engine knows a check's last signal, so the provider
  stays stateless and never reads app state.
- **Config** (schema-driven dialog): `intervalSeconds` (how often the agent is expected to
  ping) + `graceSeconds` (allowed lateness). The probe is **up** while
  `now − LastSignalUtc ≤ interval + grace`, **down** once overdue, and down until the first
  ping (`LastSignalUtc == null` ⇒ infinitely overdue).
- **Metric:** `seconds_since_heartbeat` (gauge, declared like any Phase-2a metric and kept
  by `MetricsValidator`). Emitted whenever there has been a ping; a "never pinged" age is
  not a number, so no metric is written that tick.

Outcomes map onto the existing `FailType` (incidents / alerts / `/api/status` / rollups
unchanged):

| Condition | `FailType` | State |
|---|---|---|
| ping within `interval + grace` | `NoFail` | up |
| overdue (or never pinged) | `Unreachable` | down |

### The ping endpoint & token

- On **create** of a `heartbeat` check the service generates an unguessable, URL-safe token
  (128-bit CSPRNG hex) and stamps `LastHeartbeatUtc = now` (so a fresh check has its full
  interval + grace before it can flip down).
- **`GET`/`POST /heartbeat/{token}`** — anonymous, rate-limited, empty-body **204** on a
  match / flat **404** for any unknown or rotated token. The token *is* the credential, so
  there is no user identity; the lookup is a single atomic, partial-unique-indexed timestamp
  `UPDATE` (no entity is materialised, so the token never enters a log). The public face is
  the **Web** app (the API service is internal-only); it forwards to the internal sink.
- The token is treated as a **secret**: it never rides the anonymous read VM / public status
  API. The edit dialog fetches it over an **authenticated** path
  (`GET /statuscheck/{id}/heartbeat`) to render the ping URL, and **Regenerate**
  (`POST …/heartbeat/regenerate`) rotates it — the old URL stops working immediately because
  the token is the lookup key.

## Roadmap

| Phase | Scope |
|---|---|
| **1** | Provider seam + HTTP parity (this). Registry, `HttpCheckProvider`, schema-driven edit dialog, migration, probe safety, trust-boundary doc. No new providers/metrics. |
| **2** | AI/LLM canary + agent-heartbeat providers; typed `MetricDefs` + retention/query semantics; dashboard metric rendering. |
| **3** | More built-in providers: TCP port, DNS, TLS cert-expiry, ICMP ping. |
| **4** | Out-of-process plugin protocol (any-language, untrusted-safe, sandboxed). The only tier permitted to run untrusted code. |
