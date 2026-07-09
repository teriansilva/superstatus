# SuperStatus — current-state architecture

This document describes SuperStatus as it exists in this branch. It is a
snapshot, intended to bring a new contributor or operator up to speed without
having to read every file.

It does **not** describe the deployment topology — that lives separately in
`infrastructure-docs/` and the devops plan tracked in the repo issues. The
**visual design language** (tactical-HUD tokens, Blazor primitives, surface
conventions) lives in [`design-system.md`](design-system.md).

## Overview

SuperStatus is a self-hosted status-page application. Operators register
external services to monitor; a background job polls them and records
uptime, response time, and failure classifications, then renders a public
dashboard with historical charts. Authenticated operators manage the checks
and write incident reports through a Blazor admin UI — incidents are
operator-managed today; failed checks do **not** auto-open them.

## Stack

| | |
|---|---|
| Runtime | .NET 9 |
| Orchestration | .NET Aspire 9.4 (`SuperStatus.AppHost`) — dev/local only |
| Web UI | Blazor Server (interactive server rendering) + MudBlazor 8 + Microsoft FluentUI components |
| API | ASP.NET Core minimal APIs + Hellang ProblemDetails + Swashbuckle (Swagger UI in non-prod) |
| Auth provider | OpenIddict on top of ASP.NET Identity (separate `SuperStatus.Identity` service) |
| Token client (Web→Api) | Duende.AccessTokenManagement (client-credentials flow) |
| Background jobs | PeriodicTimer hosted services (BackgroundService; issue #84 replaced Quartz.NET) |
| Persistence | Entity Framework Core 9 → PostgreSQL (single server, two logical databases) |
| Tests | MSTest + `Aspire.Hosting.Testing` `DistributedApplicationTestingBuilder` |
| i18n | JSON resource files in `locales/` (`EN_US.json`, `desc.json`) |

## Solution / project map

`SuperStatus.sln` contains nine projects:

| Project | Type | Role |
|---|---|---|
| `SuperStatus.AppHost` | Aspire host (`Exe`) | Local-only orchestrator. Spins up Postgres + pgAdmin and the three application services with environment wiring. Not used in container deployments. |
| `SuperStatus.ServiceDefaults` | classlib | Shared `AddServiceDefaults()` extension — OpenTelemetry, default health endpoints, resilience handler, service discovery. Referenced by all three runtime services. |
| `SuperStatus.ApiService` | ASP.NET Core Web | REST API for status checks and incidents; runs the status-check + cleanup hosted-service schedulers. |
| `SuperStatus.Identity` | ASP.NET Core Web | OpenIddict authorization server + user/role management UI (Razor Pages + MVC controllers). |
| `SuperStatus.Web` | ASP.NET Core Web | Blazor Server frontend — public dashboard + admin UI. OIDC client to Identity, HTTP client to ApiService. |
| `SuperStatus.Configuration` | classlib | Loads `SuperStatus.config.json` into a static config object on startup. |
| `SuperStatus.Data` | classlib | EF entities, view models, repositories, paged-result extensions. Defines the application schema. |
| `SuperStatus.DataAccess` | classlib | Holds EF migrations for the application database. |
| `SuperStatus.Services` | classlib | Domain services — `StatusCheckService`, `IncidentService`. |
| `SuperStatus.Tests` | MSTest project | Aspire smoke test against Web `/`, plus a `WebApplicationFactory`-based Identity first-run suite. |

## Service topology

Three long-running ASP.NET services plus one PostgreSQL server:

```
                    ┌────────────────────────────┐
   browser ────────►│  SuperStatus.Web           │
                    │  (Blazor Server, MudBlazor)│
                    └─────┬──────────┬───────────┘
                          │          │
                  OIDC    │          │ Bearer (client_credentials)
                  redirect│          ▼
                          │   ┌──────────────────────┐
                          │   │ SuperStatus.ApiService│
                          │   │ (Scheduler, Swagger) │
                          │   └─────┬─────────────────┘
                          ▼         │
                    ┌─────────────┐ │   EF Core
                    │ Identity    │ │  ┌─────────────┐
                    │ (OpenIddict)│ ├─►│  PostgreSQL │
                    └─────┬───────┘ │  │             │
                          │ EF Core │  │  · SuperStatusDb
                          └─────────┴──┤  · SuperStatusIdentityDb
                                        └─────────────┘
```

Browser-facing endpoints:

- **Web** (`SuperStatus.Web`) — dashboard, incident detail pages, admin pages.
- **Identity** (`SuperStatus.Identity`) — OIDC `connect/authorize`, `connect/token`, `connect/logout`, plus Razor Pages for login/register/profile. Browsers are redirected here for the login flow.

Internal endpoints (not directly browser-facing in production):

- **ApiService** — REST endpoints called server-side from Web. The two
  injected env vars `IDP_HTTP` and the API base URL determine wiring.

## Application database (`SuperStatusDb`)

Migration: `SuperStatus.Data/Migrations/SuperStatusDbMigration/20251014212447_InitialCreate.cs`.
No follow-up migrations.

Tables (logical names; EF prefixes some with the DbSet name):

| Table | Key columns | Notes |
|---|---|---|
| `StatusCheckSet` | `Id` | Per-target config: `Title`, `StatusCheckUrl`, `ExpectedStatusCode`, `ExpectedResponseTimeInMs`, `Enabled`, `ServiceLogoUrl`, `Description`, `IsWebHookOnErrorEnabled`, `WebHookOnErrorUrl`, `ThrottleWebHookToExecuteOnlyEveryXMinutes` |
| `IncidentSet` | `Id` | `Title`, `Description`, `Created`, `Resolved`, `VisibleToPublic`, `AuotmaticallyGeneratedReport` *(typo preserved from schema)* |
| `HistoricalStatusDataSet` | `Id`, FK→`StatusCheckSet`, FK→`IncidentSet` (nullable) | One row per check execution: `HttpStatusCode`, `ResponseTimeInMs`, `TimeOfCheckUTC`, `CheckFailed`, `FailType` (`StatusCode=0`, `ResponseTime=1`, `NoFail=2`, `Unreachable=3`) |
| `HistoricalStatusActionSet` | `Id`, unique FK→`HistoricalStatusData` | Records that a webhook was fired against a given check result; `ActionType` enum (`Webhook=0`), `TimeOfExecutionUTC` |

The `IncidentId` FK on historical data exists but no code path currently sets
it automatically — incident creation is operator-driven (see
`IncidentService`).

## Identity database (`SuperStatusIdentityDb`)

Migration: `SuperStatus.Data/Migrations/SuperStatusIdentityDbMigration/20250817105236_InitialCreateIdentity.cs`.

Holds the standard ASP.NET Identity tables (`AspNetUsers`, `AspNetRoles`,
`AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserRoles`,
`AspNetUserTokens`, `AspNetRoleClaims`) plus OpenIddict's
`OpenIddictApplications`, `OpenIddictAuthorizations`, `OpenIddictScopes`,
`OpenIddictTokens`.

On startup, `Identity/Program.cs` calls `SuperStatusIdentityDbInitializer.Seed`,
which (subject to `APPLY_MIGRATIONS`) runs `MigrateAsync` on the identity DB
and registers a single OpenIddict client (`aspNetCoreAuth`) pointing at
`WEBAPP_HTTP + /signin-oidc`. It does **not** create any ASP.NET Identity
user — the first admin is created interactively by the operator via the
first-run UI flow at `/Identity/Account/Setup` (see "Auth flow" below).

## Auth flow

**End user → Web → Identity** (interactive):

1. Browser hits `https://<web>/`.
2. Anonymous request triggers the OIDC challenge configured in
   `Web/Program.cs`. The Web app is registered with Identity as client
   `aspNetCoreAuth` (authorization-code flow with PKCE).
3. Browser is redirected to `IDP_HTTP`/connect/authorize on Identity.
4. After login (or existing cookie), Identity issues an authorization code
   → Web exchanges it for an ID + access token at `connect/token`.
5. Web stores cookies; subsequent requests are authenticated.

**First-run setup** (`SuperStatus.Identity/Areas/Identity/Pages/Account/`):

On a fresh database the OIDC challenge step lands on Identity's Login page
with zero users in `AspNetUsers`. `LoginModel.OnGetAsync` detects this and
redirects to `/Identity/Account/Setup`, passing the OIDC `returnUrl`
through. The operator submits email + password; `SetupModel.OnPostAsync`
creates the user, assigns the `Administrator` role, signs them in via the
Identity cookie, and `LocalRedirect`s back to `returnUrl` — at which point
`AuthorizationController.Authorize()` re-authenticates from the now-valid
cookie and continues issuing the code. Once one user exists, `Setup` and
the other AspNetCore.Identity.UI signup pages (Register, ForgotPassword,
ResetPassword, ExternalLogin, ConfirmEmail) all return 404; the operator
manages their account through `/Identity/Account/Manage/`.

**Web → ApiService** (server-side):

- For endpoints requiring user identity, Web forwards the user's bearer
  token (extracted via `GetTokenAsync("access_token")`) — see `AdminApiClient`.
- For unauthenticated public endpoints (`StatusApiClient`,
  `IncidentApiClient`), no token is sent.
- A separate **client-credentials** registration (`apiservice`) is wired up
  via `Duende.AccessTokenManagement` for machine-to-machine calls without a
  user context.

**ApiService validation:**

- JWT bearer with the authority set from `IDP_HTTP`. `[Authorize]` is applied
  on the few mutating endpoints; reads are anonymous.

## REST API surface (`SuperStatus.ApiService`)

Endpoints mapped in `SuperStatus.ApiService/Program.cs` `ConfigureEndpoints`
(see the file for the authoritative, current list):

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET` | `/statuscheck` | none | Paged list of `StatusCheckViewModel` |
| `GET` | `/statuscheck/gethistoricaldata/{id}` | none | Daily aggregates for the past N days for one check |
| `GET` | `/statuscheck/{id}/day/{date}` | none | One day's detail for a check |
| `GET` | `/statuscheck/{id}/recent` | none | Most recent ticks for a check |
| `GET` | `/statuscheck/summary` | none | Dashboard summary |
| `POST` | `/statuscheck/edit` | required | Create/update a status check |
| `POST` | `/statuscheck/{id}/run-now` | required | Trigger an immediate check |
| `DELETE` | `/statuscheck/{id}` | required | Delete a check |
| `GET` / `POST` | `/settings`, `/settings/onboarded` | mixed | Site settings |
| `GET` | `/incidents` | none | Incidents grouped by date |
| `POST` | `/incidents/edit` | required | Create/update an incident |
| `GET` | `/admin/webhook-log` | required | Webhook delivery log |

Cross-cutting:

- IP-based token-bucket rate limiter (defaults: 100 tokens/min, replenish 1/min) — returns 429 with `Retry-After`.
- Hellang ProblemDetails standardizes errors; full details only outside Production.
- Swagger UI mounted in Development/QA only.
- Health endpoints come from `AddServiceDefaults()` — `/health` and `/alive` are mapped by `MapDefaultEndpoints()` in **every** environment, so the docker-compose healthchecks work in `Staging` / `Production`.

## Background jobs (hosted services)

Both run as `PeriodicTimer`-driven `BackgroundService`s (issue #84 — `StatusCheckSchedulerService` / `DbCleanupSchedulerService`); each tick is fully awaited before the next, so ticks never overlap. Gated on `RunJobAtStartup`.

| Tick | Scheduler interval | What it does |
|---|---|---|
| `SuperStatusCheckJob.RunDueChecksAsync` | `JobIntervallInSeconds` (10s) — the *scheduler tick* | Lists enabled checks, selects those **due** by their **per-check `IntervalSeconds`** (#82) widened by **exponential backoff** on `ConsecutiveFailures` (#83); fans out **bounded** by `MaxConcurrentChecks` (#78), one DI scope per check. Per check: `ExecuteStatusCheck` → `SaveStatusCheckResult` → `RecordCheckOutcomeAsync` (backoff counter) → `RunPostStatusCheckTasks` **once** (#75) → `SaveStatusCheckAction`. |
| `SuperStatusCleanUpJob.RunCleanupAsync` | `DbCleanUpJobIntervallInMinutes` (10m) | Single bulk `DELETE` of `HistoricalStatusData` + `WebhookExecutionLog` rows older than `StatusCheckGraphViewMaxDays` (30) (#80, #107). |

`StatusCheckService.ExecuteStatusCheck` is currently **HTTP GET only**. Fail
classification, in priority order: `Unreachable` (request threw) → `StatusCode`
(mismatch) → `ResponseTime` (slower than expected) → `NoFail`. Webhook
notifications are HTTP GET to the configured URL and are throttled per check
by the timestamp of the last `HistoricalStatusAction`.

## Web UI

`SuperStatus.Web/Components/`:

- `App.razor`, `Routes.razor`, `_Imports.razor` — Blazor app shell.
- `Layout/` — `MainLayout.razor` (MudLayout + theming), navigation drawer, header/footer.
- `Pages/Home.razor` — public dashboard. Renders `StatusCheckList` and `IncidentList`. Auth-gated FABs for adding checks/incidents.
- `Pages/Admin.razor` — admin view, gated by `[Authorize]`.
- `Pages/Error.razor` — global error page.
- `StatusCheckOverview/StatusCheckList.razor` + `StatusCheckOverviewCard.razor` — auto-refreshing list polling `/statuscheck` every `StatusCheckViewRefreshIntervalInSeconds`.
- `StatusCheckOverview/StatusCheckHistoricalGraph.razor` — historical chart driven by `/statuscheck/gethistoricaldata/{id}`.
- `IncidentOverview/IncidentList.razor` — incidents grouped by date.

Three thin HTTP clients in the project root:

- `StatusApiClient.cs`, `IncidentApiClient.cs` — anonymous reads.
- `AdminApiClient.cs` — extracts bearer token from the user session and adds it to outbound requests.

## Configuration

Two layers feed runtime configuration:

1. **`SuperStatus.config.json`** — loaded statically by
   `SuperStatus.Configuration.SuperStatusConfig`. The `SuperStatusConfig`
   block (UI branding/theme, scheduler intervals, retention windows) is
   exposed as static properties and consumed at runtime. The file also
   contains a `SuperStatusCheckConfig` array of seed `StatusCheck`
   definitions, but it is **not** read by any code today —
   `SuperStatusDbInitializer.SeedStatusChecks()` hardcodes a placeholder
   pair (Google + GitHub) instead. The array is dead config.
2. **`appsettings.{json,Development.json}`** per service — connection strings,
   rate-limit thresholds, ApplicationInsights connection string,
   `AllowedHosts` (currently `*`).

Aspire's AppHost injects the following at runtime in development:

| Variable | Consumed by | Purpose |
|---|---|---|
| `ConnectionStrings__SuperStatusDb` | ApiService | Postgres → `SuperStatusDb` |
| `ConnectionStrings__SuperStatusIdentityDb` | Identity | Postgres → `SuperStatusIdentityDb` |
| `IDP_HTTP` | Web, ApiService | Identity base URL (HTTPS preferred, HTTP fallback in `DEBUG` builds) |
| `WEBAPP_HTTP` | Identity | Web frontend URL — used to validate redirect URIs |

In a non-Aspire deployment all four must be set explicitly (env vars or
`appsettings.Production.json`).

## Tests

Two MSTest classes live in `SuperStatus.Tests/`:

- `WebTests` — boots the full Aspire app via `DistributedApplicationTestingBuilder`, waits for the `webfrontend` resource to become ready (max 30s), and asserts `GET /` returns `200 OK`.
- `IdentityFirstRunTests` — `WebApplicationFactory<SuperStatus.Identity.TestEntryPoint>` against an in-memory `SuperStatusIdentityDb`, covering the Login → Setup redirect when zero users, the disabled signup pages 404'ing, and the Setup POST creating an Administrator + setting the Identity cookie.

## Localization

`locales/EN_US.json` holds UI strings; `locales/desc.json` appears to hold
descriptions. The wiring inside the Web project that consumes these is not
covered here — adding a locale means adding a sibling JSON file and registering
it on startup.

## Known limitations / gaps (current state)

These are observations, not blockers. Track separately as issues.

- **HTTP-only checks.** `StatusCheckService.ExecuteStatusCheck` only does HTTP GET. TCP / DNS / TLS-validity / ICMP checks are out of scope today.
- **Webhook double-invocation.** `SuperStatusCheckJob.Execute` calls `RunPostStatusCheckTasks` after `SaveStatusCheckResult`, but `ExecuteStatusCheck` has already invoked it once before returning. The second call is normally throttled to a no-op (the action created by the first call satisfies `IsWebhookThrottleInEffect`), but if `StatusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes == 0` the throttle check passes and the webhook fires twice per failure. Either the call inside `ExecuteStatusCheck` should be removed, or the call in the job should be — pick one.
- **`AdminApiClient.GetStatusAsync` references an unmapped endpoint.** It GETs `/admin/statuscheck`, which is not mapped on the API, so that path returns 404 at runtime. (`POST /incidents/edit`, previously also unmapped, is now mapped.)
- **Auto-incidents are opt-in per check.** Failed checks do not open incidents unless a check has `AutoIncidentEnabled` set; the `AutoIncidentWorker` / `AutoIncidentCoordinator` then manage the AI-authored report. Incidents are otherwise operator-managed.
- **Webhook notifications use HTTP GET.** No payload, no retry, no signing.
- **`AllowedHosts: "*"`** in all three services. Tighten for production.
- **No structured rate limiting on Identity's auth endpoints.** Rate limiter is registered globally but the auth flow paths are not specifically protected.
- **Schema typo `AuotmaticallyGeneratedReport`** in `Incident`. Deferred until first non-trivial migration to avoid an early breaking rename.

## Glossary of environment variables an operator will set

| Variable | Required by | Notes |
|---|---|---|
| `ConnectionStrings__SuperStatusDb` | ApiService | Standard Postgres conn string. EF migrations apply on startup when `APPLY_MIGRATIONS` is unset or `true`. |
| `ConnectionStrings__SuperStatusIdentityDb` | Identity | Same Postgres server, separate database. |
| `APPLY_MIGRATIONS` | ApiService, Identity | Apply EF migrations on startup (default `true`). Set `false` to opt out, e.g. while debugging a suspect migration. |
| `IDP_HTTP` | Web, ApiService | Base URL the services use to reach Identity (back-channel: discovery, token, JWKS). Behind a reverse proxy this is also the browser-facing URL. |
| `IDP_PUBLIC_HTTP` | Web, ApiService, Identity | Browser-facing Identity authority for the single-host self-host stack, when it differs from `IDP_HTTP`. Identity pins it as the issuer; Web/API route their back-channel calls to `IDP_HTTP` while keeping this as the public authority. Unset in the reverse-proxy deployment. |
| `WEBAPP_HTTP` | Identity | External base URL of the Web frontend. Used for redirect URI validation. |
| `ASPNETCORE_ENVIRONMENT` | all three | `Production` in production. Controls Swagger and ProblemDetails verbosity. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | all three | Set `true` when sitting behind nginx/Traefik so the framework honours `X-Forwarded-*`. |
