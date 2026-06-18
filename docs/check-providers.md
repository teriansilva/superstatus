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
  Descriptor { TypeId, DisplayName, Icon, ConfigSchema (versioned), MetricDefs }
  Task<ProbeResult> ProbeAsync(ProbeContext ctx, CancellationToken ct)

ProbeResult  { FailType, LatencyMs, Reachable, HttpStatusCode?, MetricsJson?, Message }
ProbeContext { CheckId, CheckTitle, ConfigJson (validated), per-probe Timeout }
```

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
- `HistoricalStatusData` gains a nullable `MetricsJson` (unused in Phase 1).

## Roadmap

| Phase | Scope |
|---|---|
| **1** | Provider seam + HTTP parity (this). Registry, `HttpCheckProvider`, schema-driven edit dialog, migration, probe safety, trust-boundary doc. No new providers/metrics. |
| **2** | AI/LLM canary + agent-heartbeat providers; typed `MetricDefs` + retention/query semantics; dashboard metric rendering. |
| **3** | More built-in providers: TCP port, DNS, TLS cert-expiry, ICMP ping. |
| **4** | Out-of-process plugin protocol (any-language, untrusted-safe, sandboxed). The only tier permitted to run untrusted code. |
