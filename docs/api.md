# Public API

Stable, anonymous, machine-readable endpoints for programmatic consumers
(load balancers, on-call dashboards, external embeds, oncall pagers).

The Web UI's own endpoints under `/statuscheck/...` and `/incidents`
remain available but are **not** part of this contract — they are shaped
for the Blazor Server client and may change.

## Versioning

- v1 of the public API is what's documented below.
- **Additive** changes within v1 are allowed (new fields, new endpoints).
- **Breaking** changes (rename / remove / type-change a field) require a
  new endpoint group, e.g. `/api/status/v2`, plus a deprecation header
  on v1 for at least one release cycle.
- Every response carries the header `X-SuperStatus-Api-Version: 1` so
  consumers can detect the contract.

## CORS + caching

- `Access-Control-Allow-Origin: *` — read-only, no credentials, safe to
  fetch from any external embed.
- `Cache-Control: no-store, max-age=0` — reverse proxies always serve
  fresh.

## Rate limiting

The endpoints inherit the project-global IP rate limit (default
**100 requests / minute / IP**), enforced before the handler runs.
External consumers polling more aggressively will get a `429`.

---

## `GET /api/status`

Single JSON document with the operational truth the public dashboard
shows.

### Response shape (v1)

```json
{
  "overall": "degraded",
  "generated_utc": "2026-05-28T19:12:07Z",
  "services": [
    {
      "id": 9,
      "title": "Payments gateway",
      "state": "degraded",
      "last_checked_utc": "2026-05-28T19:12:05Z",
      "last_latency_ms": 1420,
      "expected_status_code": 200,
      "expected_response_time_ms": 800
    }
  ],
  "incidents_open": [
    {
      "id": 42,
      "title": "Payments gateway — elevated latency",
      "started_utc": "2026-05-28T19:05:00Z"
    }
  ]
}
```

### `overall` rule

Computed in `PublicStatusApi.ComputeOverall`:

- `up` — every service is `up` AND no public open incidents.
- `degraded` — at least one service is `degraded`, OR at least one
  open public incident exists.
- `down` — at least one service is `down`.

### Per-service `state`

Mapped from the internal `FailType` by `PublicStatusApi.MapStateLabel`:

| `FailType`        | `state`      |
|---|---|
| `NoFail`         | `up`         |
| `ResponseTime`   | `degraded`   |
| `StatusCode`     | `down`       |
| `Unreachable`    | `down`       |
| (no history yet) | `unknown`    |

`last_checked_utc`, `last_latency_ms` are `null` when the service has no
recorded ticks yet. `last_latency_ms` is also `null` when the most-recent
tick was a transport failure (`CheckFailed=true`).

### Open-incidents privacy

`incidents_open` includes only incidents where **both** flags are true:

- `Resolved == false`
- `VisibleToPublic == true`

Operator-drafted internal incidents (typically `VisibleToPublic=false`)
are never exposed by this endpoint. The same filter is applied at the
repository layer so the rule cannot be lost in transport.

### Stability

A snapshot test (`PublicStatusApiTests`) asserts the v1 JSON property
set + `overall` mapping. Additive PRs (new fields) update the snapshot
deliberately; renames / removals are blocked.

### Not (yet) in v1

- Per-service severity / affected-service-ids on incidents (deferred to
  the `Incident` state-machine work in #106).
- Per-service history endpoint (e.g.
  `/api/status/services/{id}/history?days=N`) — sibling follow-up.
- Server-Sent Events / WebSocket push.
- RSS / Atom incident feed.
- v2 with richer SLO data.
