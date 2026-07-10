using Microsoft.Extensions.Caching.Memory;
using SuperStatus.ApiService;
using Hellang.Middleware.ProblemDetails;
using Hellang.Middleware.ProblemDetails.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using SuperStatus.ServiceDefaults;
using SuperStatus.ApiService.Configuration.ErrorHandling;
using SuperStatus.ApiService.Configuration.Routing;
using SuperStatus.ApiService.Configuration.Settings;
using SuperStatus.Configuration;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Utilities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Scheduler;
using SuperStatus.Services;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.RateLimiting;
using ProblemDetailsOptions = Hellang.Middleware.ProblemDetails.ProblemDetailsOptions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

ConfigureServices(builder);
ConfigureReverseProxy(builder);
ConfigureAuthentication(builder);
ConfigureScheduler(builder);
WebApplication app = builder.Build();

ConfigureMiddleware(app);

// APPLY_MIGRATIONS=false to opt out (e.g. if a migration is suspect).
// Sample status checks seed in dev by default; other environments can opt
// in by setting SEED_SAMPLE_DATA=true (used by docker-compose.staging.yml
// so the dashboard has visible sample checks on staging without an operator
// having to register checks manually).
var applyMigrations = (Environment.GetEnvironmentVariable("APPLY_MIGRATIONS") ?? "true")
    .Equals("true", StringComparison.OrdinalIgnoreCase);
var seedSampleData = EnvironmentUtilities.IsDevEnvironment(app.Environment.EnvironmentName)
    || (Environment.GetEnvironmentVariable("SEED_SAMPLE_DATA") ?? "")
        .Equals("true", StringComparison.OrdinalIgnoreCase);

await SuperStatusDbInitializer.Seed(
    app.Services,
    applyMigrations: applyMigrations,
    seedSampleData: seedSampleData);

// #377: the public demo seeds its admin directly into the identity DB, so it never
// runs the first-run setup wizard that stamps SiteSettings.OnboardedUtc. Without that
// stamp Home.razor treats the instance as not-onboarded and redirects every anonymous
// visitor to /admin → login — i.e. the demo's public status page would sit behind a
// login wall after every hourly reset. Mark it onboarded so the dashboard renders (with
// the SEED_SAMPLE_DATA sample check). Idempotent: CompleteOnboardingAsync only stamps
// the first time. Demo-only; a real install still onboards through the wizard.
if (DemoMode.IsEnabledFromEnvironment())
{
    using var demoScope = app.Services.CreateScope();
    await demoScope.ServiceProvider
        .GetRequiredService<ISiteSettingsService>()
        .CompleteOnboardingAsync();
}

// #293: seed the default SLA + assign it to any SLA-less check at startup —
// idempotent, fail-fast. Classification reads the threshold ONLY through the
// link, so a check without one would throw on its next tick; refusing to
// start beats that. (#291 Phase D: the legacy-field → linked-target startup
// backfill is GONE — the DropLegacyEmbeddedNotificationColumns migration
// translated any remaining legacy config in raw SQL before dropping the
// columns, so there is nothing left for app code to read.)
using (IServiceScope backfillScope = app.Services.CreateScope())
{
    const int backfillAttempts = 3;
    var slaService = backfillScope.ServiceProvider
        .GetRequiredService<SuperStatus.Services.Services.ISlaNormalizationService>();
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            await slaService.BackfillAsync(dryRun: false);
            break;
        }
        catch (Exception ex) when (attempt < backfillAttempts)
        {
            app.Logger.LogWarning(ex, "SLA backfill attempt {Attempt}/{Max} failed; retrying", attempt, backfillAttempts);
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "SLA backfill failed after {Max} attempts — refusing to start with checks lacking a threshold source", backfillAttempts);
            throw;
        }
    }
}

ConfigureEndpoints(app);
UseAuthentication(app);

app.Run();
return;

static void ConfigureAuthentication(WebApplicationBuilder builder)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme =
            JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme =
            JwtBearerDefaults.AuthenticationScheme;
    }).AddJwtBearer(options =>
    {
        // IDP_HTTP = internal/back-channel authority (metadata + JWKS reachable
        // from inside the container network). IDP_PUBLIC_HTTP = the public issuer
        // tokens carry; falls back to IDP_HTTP when they're the same host.
        var idpInternal = Environment.GetEnvironmentVariable("IDP_HTTP");
        // Treat an empty/whitespace IDP_PUBLIC_HTTP the same as unset (fall back to the
        // internal authority), so a blank value can never yield an empty OIDC authority.
        var idpPublicRaw = Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP");
        var idpPublic = string.IsNullOrWhiteSpace(idpPublicRaw) ? idpInternal : idpPublicRaw;

        // Fail fast on a loopback public issuer paired with an HTTPS back-channel
        // authority (a TLS deploy that left IDP_PUBLIC_HTTP at the localhost default).
        IdpAuthority.Validate(idpPublic, idpInternal);

        // Validate against the public issuer; fetch metadata/JWKS over the
        // internal host via the rewrite handler (not installed when equal).
        options.Authority = idpPublic;
        if (IdpBackchannelRewriteHandler.TryCreate(idpPublic, idpInternal) is { } rewrite)
        {
            options.BackchannelHttpHandler = rewrite;
        }
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false
        };
    });

    builder.Services.AddAuthorization();
}

static void UseAuthentication(WebApplication app)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

static void ConfigureMiddleware(WebApplication webApplication)
{
    webApplication.UseProblemDetails(); // needs to be before most other middleware

    if (EnvironmentUtilities.IsDevOrQaEnvironment(webApplication.Environment.EnvironmentName))
    {
        webApplication.UseSwagger();
        webApplication.UseSwaggerUI();
    }

    webApplication.UseForwardedHeaders(); // also relevant for reverse proxy scenarios

    webApplication.UseRouting(); // before authentication, authorization, cors, rate limiter and endpoints

    webApplication.UseCors();

    webApplication.MapDefaultEndpoints();

    webApplication.UseRateLimiter();

    webApplication.MapControllers();
}
void ConfigureReverseProxy(WebApplicationBuilder applicationBuilder)
{
    applicationBuilder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.AddServerHeader = false;
    });

    applicationBuilder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedPrefix;
    });
}

void ConfigureServices(WebApplicationBuilder applicationBuilder)
{

    applicationBuilder.AddServiceDefaults();

    IServiceCollection services = applicationBuilder.Services;

    services.AddApplicationServices(applicationBuilder.Configuration);

    // #136: in-process cache for the heavy dashboard summary (short TTL).
    services.AddMemoryCache();

    services.AddHttpContextAccessor();

    services.AddProblemDetails(ConfigureProblemDetails)
        .AddProblemDetailsConventions();

    AddRateLimiter(services, applicationBuilder.Configuration);
    AddPublicApiCors(services);

    AddControllers(services);
    AddSwagger(services);
}

// Issue #108. Scoped CORS policy for the public /api/status endpoint only.
// Applied via `.RequireCors(...)` on the endpoint group so admin/operator
// routes never accidentally inherit the wildcard origin.
void AddPublicApiCors(IServiceCollection services)
{
    services.AddCors(options =>
    {
        options.AddPolicy(PublicStatusApi.CorsPolicyName, policy =>
        {
            policy.AllowAnyOrigin()
                  .WithMethods("GET")
                  .WithHeaders("Accept", "Content-Type");
        });
    });
}

void ConfigureScheduler(WebApplicationBuilder builder)
{
    // Issue #84: the status-check + cleanup loops run as plain hosted services
    // on PeriodicTimers (no Quartz). RunJobAtStartup keeps its meaning — it
    // gates whether the scheduler runs at all. Graceful drain on shutdown is
    // inherent: BackgroundService.StopAsync cancels the token and awaits
    // ExecuteAsync, and each tick is fully awaited before the next.
    if (SuperStatusConfig.RunJobAtStartup)
    {
        // Issue #78: bounded fan-out degree for the status-check tick.
        builder.Services.AddSingleton(new SchedulerConcurrencyOptions(SuperStatusConfig.MaxConcurrentChecks));

        // Issue #84: tick intervals, derived from config.
        builder.Services.AddSingleton(new SchedulerIntervals(
            StatusTick: TimeSpan.FromSeconds(Math.Max(1, SuperStatusConfig.JobIntervallInSeconds)),
            CleanupTick: TimeSpan.FromMinutes(Math.Max(1, SuperStatusConfig.DbCleanUpJobIntervallInMinutes))));

        // Tick orchestrators are singletons (they open their own per-check DI
        // scopes internally), exposed via their tick interfaces.
        builder.Services.AddSingleton<IStatusCheckTick, SuperStatusCheckJob>();
        builder.Services.AddSingleton<IDbCleanupTick, SuperStatusCleanUpJob>();

        builder.Services.AddHostedService<StatusCheckSchedulerService>();
        builder.Services.AddHostedService<DbCleanupSchedulerService>();
        // #168: drains the auto-incident queue the scheduler feeds. Paired with the
        // scheduler — only meaningful when checks are running.
        builder.Services.AddHostedService<AutoIncidentWorker>();
    }

    // Issue #249 (epic #248): the nightly update check is independent of the
    // status-check scheduler — a status page should surface update availability
    // regardless of whether this instance runs checks — so it's registered
    // outside the RunJobAtStartup gate. Read-only; honours the operator toggle.
    builder.Services.AddSingleton(UpdateCheckOptions.Daily);
    builder.Services.AddHostedService<UpdateCheckWorker>();

    // Issue #334: applies the daily automatic update, when the operator has switched
    // it on. Registered unconditionally for the same reason as the check above — and
    // because it self-guards: it no-ops when the toggle is off, when the update engine
    // is opted out (CanApply=false), or when no update is available. It is the ONLY
    // scheduler for updates; Watchtower ships with no schedule of its own.
    builder.Services.AddSingleton(AutoUpdateOptions.Default);
    builder.Services.AddHostedService<AutoUpdateWorker>();
}
void AddControllers(IServiceCollection services)
{
    services.AddControllers(options =>
    {
        options.Conventions.Add(new RouteTokenTransformerConvention(new RestfulRouteTransformer()));
    }).ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = actionContext =>
            new BadRequestObjectResult(actionContext.ModelState);
    }).AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
    });
}

void AddRateLimiter(IServiceCollection services, ConfigurationManager configuration)
{
    services.Configure<RateLimitSettings>(configuration.GetSection("RateLimiting"));

    services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            RateLimitSettings rateLimitSettings = context.RequestServices
                .GetRequiredService<IOptions<RateLimitSettings>>()
                .Value;


            // limit by IP address
            string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = rateLimitSettings.TokenLimit,
                TokensPerPeriod = rateLimitSettings.TokensPerPeriod,
                ReplenishmentPeriod = rateLimitSettings.ReplenishmentPeriod,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0      // reject immediately when empty
            });
        });

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                // Return Retry-After in seconds (as an integer)
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);
            }

            await context.HttpContext.Response.WriteAsync(
                "Too many requests - please try again later.", cancellationToken);
        };
    });

}
void ConfigureProblemDetails(ProblemDetailsOptions options)
{
    options.IncludeExceptionDetails = (ctx, _) =>
    {
        var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
        return EnvironmentUtilities.IsDevOrQaEnvironment(env.EnvironmentName);
    };

    options.MapExceptionsToResponses();

    options.ShouldLogUnhandledException = (_, _, problemDetails) =>
        problemDetails.Status is >= 400;
}

void AddSwagger(IServiceCollection services)
{
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    services.AddEndpointsApiExplorer();
    services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "SuperTalk API",
            Version = "v1",
            Description = "REST API to perform operations specific to SuperTalk application",
        });

        //var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
        //string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        //c.IncludeXmlComments(xmlPath);

        c.DescribeAllParametersInCamelCase();
    });
}

static void ConfigureEndpoints(WebApplication app)
{
    app.MapGet("/statuscheck", async (IStatusCheckService statusCheckService) =>
    {
        // Always 200 — an empty collection is a valid result, not a missing
        // resource. Returning 404 here propagates as an HttpRequestException
        // through the Web client's EnsureSuccessStatusCode and crashes the
        // Blazor circuit on first render of an empty dashboard.
        IPagedResult<StatusCheckViewModel> statusCheck = await statusCheckService.GetStatusCheckViewModelSet();
        return Results.Ok(statusCheck);
    });

    app.MapGet("/statuscheck/gethistoricaldata/{id}", async (int id, IStatusCheckService statusCheckService) =>
    {
        return await statusCheckService.GetHistoricalStatusDataOverviewForRecentTimeRange(id, SuperStatusConfig.StatusCheckGraphViewMaxDays);
    });

    // #226: the whole dashboard's 30-day strips in one batched read (collapses the
    // former per-card N+1). Flat list grouped by StatusCheckId; the Web client
    // fetches it once and hands each card its slice.
    app.MapGet("/statuscheck/historical-overview", async (IStatusCheckService statusCheckService) =>
    {
        return await statusCheckService.GetHistoricalStatusDataOverviewForAllChecks(SuperStatusConfig.StatusCheckGraphViewMaxDays);
    });

    app.MapGet("/statuscheck/{id:long}/day/{date}", async (long id, string date, IStatusCheckService statusCheckService) =>
    {
        // Issue #201. Anonymous read; backs the lazy hover-detail popover on the
        // 30-day uptime strip. Returns 400 on a malformed date, 404 when the check
        // is unknown; a no-sample day comes back as Status "gap".
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d))
        {
            return Results.BadRequest();
        }
        DayDetailViewModel? detail = await statusCheckService.GetDayDetailAsync(id, d);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    });

    app.MapGet("/statuscheck/{id:long}/recent", async (long id, int? count, IStatusCheckService statusCheckService) =>
    {
        // Issue #103. Anonymous read; backs the service-detail page's
        // "RECENT // Tick history" panel. Returns 404 when the status check
        // is unknown so the page can render its not-found state instead of
        // a misleading empty list.
        StatusCheck? check = await statusCheckService.GetStatusCheck(id);
        if (check is null)
        {
            return Results.NotFound();
        }
        List<HistoricalStatusData> ticks = await statusCheckService.GetRecentTicks(id, count ?? 20);
        return Results.Ok(ticks);
    });

    // #317 Phase 2a: a check's recent metric series (the provider's declared MetricDefs +
    // per-tick values parsed from the raw-tick window's MetricsJson). Anonymous read;
    // backs the Phase-2c dashboard rendering. 404 when the check is unknown; empty samples
    // for a provider that emits no metrics (e.g. http).
    app.MapGet("/statuscheck/{id:long}/metrics", async (long id, int? count, IStatusCheckService statusCheckService, CancellationToken ct) =>
    {
        CheckMetricsViewModel? metrics = await statusCheckService.GetRecentMetricsAsync(id, Math.Clamp(count ?? 60, 1, 500), ct);
        return metrics is null ? Results.NotFound() : Results.Ok(metrics);
    });

    // #320 Phase 2b heartbeat endpoints — mapped ONLY while the heartbeat provider is
    // registered. The provider is currently parked (operator decision, 2026-07-07 —
    // see ServiceRegistration), and a disabled feature must not leave a live anonymous
    // ping sink behind: with the provider gone these routes simply 404 like any
    // unknown path. Re-registering the provider brings them back with no other change.
    if (app.Services.GetRequiredService<SuperStatus.Services.Providers.ICheckProviderRegistry>()
            .Find(SuperStatus.Services.Providers.Heartbeat.HeartbeatCheckProvider.TypeId) is not null)
    {
        // The agent-heartbeat ping sink. ANONYMOUS by design — the token IS the
        // credential, so there is no user identity. GET or POST both work (curl/wget/cron
        // friendliness). The body is always empty: 204 when a check's token matched, a flat
        // 404 for any unknown/rotated token (and for an implausibly long token, short-circuit
        // to 404 without touching the DB — same response, no oracle). Covered by the global
        // rate limiter. This endpoint is reached publicly via the Web app's /heartbeat forward;
        // the API stays internal-only.
        app.MapMethods("/heartbeat/{token}", new[] { "GET", "POST" }, async (string token, IStatusCheckService statusCheckService, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(token) || token.Length > 128) return Results.NotFound();
            bool recorded = await statusCheckService.RecordHeartbeatAsync(token, ct);
            return recorded ? Results.NoContent() : Results.NotFound();
        });

        // Operator-only read of a heartbeat check's token, so the edit dialog can render
        // the ping URL. AUTHENTICATED — the token never rides the anonymous read VM
        // (/statuscheck). 404 when the check is unknown or isn't a heartbeat check.
        app.MapGet("/statuscheck/{id:long}/heartbeat", async (long id, IStatusCheckService statusCheckService, CancellationToken ct) =>
        {
            string? token = await statusCheckService.GetHeartbeatTokenAsync(id, ct);
            return token is null ? Results.NotFound() : Results.Ok(new HeartbeatTokenResponse(token));
        }).RequireAuthorization();

        // Rotate the token (operator action). The old ping URL stops working the
        // instant this returns. AUTHENTICATED. 404 when unknown / not a heartbeat check.
        app.MapPost("/statuscheck/{id:long}/heartbeat/regenerate", async (long id, IStatusCheckService statusCheckService, CancellationToken ct) =>
        {
            string? token = await statusCheckService.RegenerateHeartbeatTokenAsync(id, ct);
            return token is null ? Results.NotFound() : Results.Ok(new HeartbeatTokenResponse(token));
        }).RequireAuthorization();
    }

    app.MapGet("/statuscheck/summary", async (IStatusCheckService statusCheckService, IIncidentService incidentService, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
    {
        // Issue #104 + #136. Anonymous read; one aggregated payload backs the
        // Home hero + per-service 30-day strips. The whole payload (including
        // the incident count) is cached for ~20 s, so a cache hit recomputes
        // NEITHER the heavy daily aggregation NOR the incident count — this is
        // what shields the DB from dashboard refresh storms. Browser cache stays
        // no-store; freshness is the server-side TTL.
        var summary = await DashboardCache.GetOrComputeAsync(cache, async () =>
        {
            int incidents30d = await incidentService.CountIncidentsInWindowAsync(30, ct);
            return await statusCheckService.GetDashboardSummaryAsync(incidents30d, ct);
        });
        http.Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return Results.Ok(summary);
    });

    // Issue #167: operator-editable branding (title / logo / accent). Public
    // read (cached ~30s, like the summary); operator write invalidates the cache.
    app.MapGet("/settings", async (ISiteSettingsService settingsService, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
    {
        var settings = await cache.GetOrCreateAsync("site-settings", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await settingsService.GetSettingsAsync(ct);
        });
        http.Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return Results.Ok(settings);
    });

    app.MapPost("/settings", async (SiteSettingsViewModel body, ISiteSettingsService settingsService, IMemoryCache cache, CancellationToken ct) =>
    {
        var saved = await settingsService.SaveSettingsAsync(body, ct);
        cache.Remove("site-settings");   // next read recomputes from the saved row
        return Results.Ok(saved);
    }).RequireAuthorization();

    // #181: mark the first-run setup wizard complete (operator action).
    app.MapPost("/settings/onboarded", async (ISiteSettingsService settingsService, IMemoryCache cache, CancellationToken ct) =>
    {
        var saved = await settingsService.CompleteOnboardingAsync(ct);
        cache.Remove("site-settings");
        return Results.Ok(saved);
    }).RequireAuthorization();

    // #241 Phase B: persist ONLY the SMTP/email-alert columns (separate from the
    // branding/AI save so an unrelated settings edit can't wipe a configured relay).
    app.MapPost("/settings/smtp", async (SiteSettingsViewModel body, ISiteSettingsService settingsService, IMemoryCache cache, CancellationToken ct) =>
    {
        var saved = await settingsService.SaveSmtpSettingsAsync(body, ct);
        cache.Remove("site-settings");
        return Results.Ok(saved);
    }).RequireAuthorization();

    // #358: persist ONLY the allowed-sign-in-hosts allowlist (separate from the
    // branding/AI/SMTP saves so an unrelated settings edit can't clear this
    // security-sensitive field). Identity reads it back via the public GET /settings.
    app.MapPost("/settings/authhosts", async (SiteSettingsViewModel body, ISiteSettingsService settingsService, IMemoryCache cache, CancellationToken ct) =>
    {
        // #358: reject a payload with any non-blank invalid / over-limit host with 422
        // and leave the stored allowlist unchanged — a typo must not silently clear a
        // hardened allowlist back to relaxed. An explicit empty list is the intentional
        // "clear" and is allowed through.
        if (!AuthHostAllowlist.TryNormalizeForWrite(body.AllowedAuthHosts, out var normalizedHosts, out var hostError))
            return Results.UnprocessableEntity(new { error = hostError });
        body.AllowedAuthHosts = normalizedHosts;
        var saved = await settingsService.SaveAuthHostsAsync(body, ct);
        cache.Remove("site-settings");
        return Results.Ok(saved);
    }).RequireAuthorization();

    app.MapPost("/statuscheck/edit", async (StatusCheckViewModelBase statusCheckToUpdate, IStatusCheckService statusCheckService, SuperStatus.Services.Services.ILinkedTargetNormalizationService linkedTargets, SuperStatus.Services.Services.ISlaNormalizationService slaService, IRepository<Sla> slaRepository, SuperStatus.Data.DatabaseContext.SuperStatusDb db, CancellationToken ct) =>
    {
        // #291 Phase D: payloads still carrying the removed legacy embedded
        // webhook/alert fields are rejected outright (the message points at
        // the release notes) — the translation window is closed.
        string? legacy = LinkedTargetsAdminApi.ValidateEditPayload(statusCheckToUpdate);
        if (legacy is not null)
            return Results.UnprocessableEntity(new { message = legacy });

        // Unknown ids are a 422 too (and checked before the save, so a bad
        // payload leaves nothing half-applied).
        if (statusCheckToUpdate.WebhookIds is { } webhookIds)
        {
            var missing = await linkedTargets.FindMissingWebhookIdsAsync(webhookIds, ct);
            if (missing.Count > 0)
                return Results.UnprocessableEntity(new { message = $"Unknown webhook id(s): {string.Join(", ", missing)}" });
        }
        if (statusCheckToUpdate.AlertProfileIds is { } alertProfileIds)
        {
            var missing = await linkedTargets.FindMissingAlertProfileIdsAsync(alertProfileIds, ct);
            if (missing.Count > 0)
                return Results.UnprocessableEntity(new { message = $"Unknown alert profile id(s): {string.Join(", ", missing)}" });
        }

        // #293: an unknown SLA id is a 422 too, checked before any write.
        if (statusCheckToUpdate.SlaId is long requestedSlaId
            && !await slaRepository.Any(s => s.Id == requestedSlaId, ct))
        {
            return Results.UnprocessableEntity(new { message = $"Unknown SLA id: {requestedSlaId}" });
        }
        bool isNewCheck = statusCheckToUpdate.Id == 0;

        // #291 (Hermes): links are the runtime source of truth, so a check
        // update without its links is a misrouting hazard — both writes
        // commit together or not at all. Service + normalization share the
        // request-scoped DbContext, so one ambient transaction covers both.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            StatusCheck saved = await statusCheckService.AddOrUpdateStatusCheck(statusCheckToUpdate);

            // Explicit id arrays replace the family's link set; omitted arrays
            // leave that family's links unchanged (Phase D: no legacy fallback).
            await linkedTargets.ApplyEditLinksAsync(saved, statusCheckToUpdate.WebhookIds, statusCheckToUpdate.AlertProfileIds, ct);

            // #293: the SLA link rides the same transaction — an explicit
            // SlaId wins; null keeps the current link (new checks land on the
            // IsDefault SLA).
            await slaService.ApplyEditSlaAsync(saved, statusCheckToUpdate.SlaId, isNewCheck, ct);
            await tx.CommitAsync(ct);
        }
        return Results.Ok();
    }).RequireAuthorization();

    // #342: batch add — create many checks from a pasted target list. The service
    // re-parses + canonicalises every line (the client parse is advisory), dedups against
    // existing checks, and creates the valid remainder in ONE transaction. A rejection
    // (unknown provider / not batch-capable, over-cap, unknown webhook/profile/SLA id, or
    // zero valid targets) is a 422 with nothing written; otherwise 200 with the per-line
    // breakdown (created id or skipped + reason).
    app.MapPost("/statuscheck/batch", async (BatchCreateChecksRequest body, IBatchCheckCreationService batchService, CancellationToken ct) =>
    {
        BatchCreateOutcome outcome = await batchService.CreateBatchAsync(body, ct);
        return outcome.Rejected
            ? Results.UnprocessableEntity(new { message = outcome.RejectionMessage })
            : Results.Ok(outcome.Response);
    }).RequireAuthorization();

    // Issue #105. Operator-only manual run.
    app.MapPost("/statuscheck/{id:long}/run-now", async (long id, IStatusCheckService statusCheckService, CancellationToken ct) =>
    {
        StatusCheck? check = await statusCheckService.GetStatusCheck(id);
        if (check is null) return Results.NotFound();
        // #312: the same resolve-or-disable gate the scheduled tick uses — a check with
        // an unknown provider type or invalid config is disabled calmly (422 + reason),
        // never a crash and never a silent default probe.
        var resolution = statusCheckService.ResolveProbe(check);
        if (resolution.IsDisabled)
            return Results.UnprocessableEntity(new { message = $"Check disabled — {resolution.DisableReason}" });
        HistoricalStatusDataViewModel result = await statusCheckService.RunCheckNowAsync(id, ct);
        return Results.Ok(result);
    }).RequireAuthorization();

    // Issue #105. Operator-only pause/resume toggle.
    app.MapPatch("/statuscheck/{id:long}/enabled", async (long id, EnabledRequest body, IStatusCheckService statusCheckService, CancellationToken ct) =>
    {
        StatusCheck? check = await statusCheckService.GetStatusCheck(id);
        if (check is null) return Results.NotFound();
        StatusCheck updated = await statusCheckService.SetEnabledAsync(id, body.Enabled, ct);
        return Results.Ok(new { id = updated.Id, enabled = updated.Enabled });
    }).RequireAuthorization();

    // Issue #164. Operator-only hard delete. Dependent rows (historical data,
    // daily rollups, webhook logs) cascade at the DB. 204 on delete; 404 if the
    // id doesn't exist.
    app.MapDelete("/statuscheck/{id:long}", async (long id, IStatusCheckService statusCheckService, CancellationToken ct) =>
    {
        bool deleted = await statusCheckService.DeleteStatusCheckAsync(id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }).RequireAuthorization();


    app.MapGet("/incidents", async (IIncidentService incidentService) =>
    {
        // Always 200 — same reasoning as /statuscheck. Empty grouping is
        // a valid result; the Web client expects a deserialisable body.
        IDictionary<DateTime, List<IncidentViewModel>> incidents = await incidentService.GetIncidentViewModelSetForDays();
        return Results.Ok(incidents);
    });

    // Issue #106 PR2: operator-only incident create/update (the IncidentEditDialog
    // posts here). Mirrors /statuscheck/edit — RequireAuthorization keeps
    // operator drafts off the anonymous surface.
    app.MapPost("/incidents/edit", async (IncidentViewModel incidentToSave, IIncidentService incidentService, CancellationToken ct) =>
    {
        IncidentViewModel saved = await incidentService.AddOrUpdateIncident(incidentToSave, ct);
        return Results.Ok(saved);
    }).RequireAuthorization();

    // Issue #107 Phase 2: operator-only webhook execution-log audit feed.
    // count is clamped (1–500) at the repository; failuresOnly narrows to
    // actual wire failures (NonSuccess/Timeout/TransportFailure).
    app.MapGet("/admin/webhook-log", async (IStatusCheckService statusCheckService, CancellationToken ct, int count = 100, bool failuresOnly = false) =>
    {
        var log = await statusCheckService.GetRecentWebhookLogAsync(count, failuresOnly, ct);
        return Results.Ok(log);
    }).RequireAuthorization();

    // Issue #241/#253: operator-only alert delivery-log audit feed. Reads the
    // repo directly (keeps StatusCheckService's ctor unchanged). count clamped
    // 1–500; failuresOnly narrows to Failed deliveries (B/C — none in Phase A).
    app.MapGet("/admin/alert-log", async (IAlertDeliveryLogRepository alertLogRepository, CancellationToken ct, int count = 100, bool failuresOnly = false) =>
    {
        var rows = await alertLogRepository.GetRecentWithCheckAsync(count, failuresOnly, ct);
        return Results.Ok(rows.Select(r => new AlertDeliveryLogViewModel(r)).ToList());
    }).RequireAuthorization();

    // Issue #241 Phase B: send a test email to verify SMTP config. On success the
    // notifier stamps SmtpVerifiedUtc. Optional body { to } overrides the default
    // recipients.
    app.MapPost("/admin/email/test", async (EmailTestRequest? body, SuperStatus.Services.Alerts.IEmailNotifier emailNotifier, CancellationToken ct) =>
    {
        var result = await emailNotifier.SendTestAsync(body?.To, ct);
        return result.Ok
            ? Results.Ok(new { ok = true, target = result.Target })
            : Results.BadRequest(new { ok = false, error = result.Detail });
    }).RequireAuthorization();

    // Issue #241 Phase C: browser Web Push. The console fetches the VAPID public key,
    // subscribes the browser, and posts the subscription here. Operator-only (the
    // enable button lives on the [Authorize] admin surface; calls ride the machine
    // token like the other /settings writes).
    app.MapGet("/api/push/vapid-key", async (ISiteSettingsService settingsService, CancellationToken ct) =>
    {
        var key = await settingsService.GetOrCreateVapidPublicKeyAsync(ct);
        return Results.Ok(new { key });
    }).RequireAuthorization();

    app.MapGet("/api/push/subscriptions/count", async (IPushSubscriptionRepository repo, CancellationToken ct) =>
    {
        var count = await repo.CountAsync(ct);
        return Results.Ok(new { count });
    }).RequireAuthorization();

    app.MapPost("/api/push/subscribe", async (PushSubscribeRequest body, IPushSubscriptionRepository repo, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(body.Endpoint) || string.IsNullOrWhiteSpace(body.P256dh) || string.IsNullOrWhiteSpace(body.Auth))
            return Results.BadRequest(new { ok = false, error = "endpoint, p256dh and auth are required" });

        // Upsert by endpoint so re-subscribing the same browser refreshes its keys
        // instead of duplicating (the DB also has a unique index as a backstop).
        var existing = await repo.GetByEndpointAsync(body.Endpoint, ct);
        if (existing is null)
        {
            await repo.AddAndSave(new PushSubscription
            {
                Endpoint = body.Endpoint,
                P256dh = body.P256dh,
                Auth = body.Auth,
                UserAgent = body.UserAgent,
                CreatedUtc = DateTime.UtcNow,
            }, ct);
        }
        else
        {
            existing.P256dh = body.P256dh;
            existing.Auth = body.Auth;
            existing.UserAgent = body.UserAgent;
            await repo.UpdateAndSave(existing, ct);
        }
        return Results.Ok(new { ok = true });
    }).RequireAuthorization();

    app.MapPost("/api/push/unsubscribe", async (PushUnsubscribeRequest body, IPushSubscriptionRepository repo, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(body.Endpoint))
            return Results.BadRequest(new { ok = false, error = "endpoint is required" });
        int removed = await repo.DeleteByEndpointAsync(body.Endpoint, ct);
        return Results.Ok(new { ok = true, removed });
    }).RequireAuthorization();

    // Issue #249 (epic #248): the running app's version + release channel, read
    // from the stamped assembly. Internal API (the api service isn't published to
    // the internet); the operator console + update-check worker consume it.
    app.MapGet("/api/version", (IAppVersionProvider version) =>
        Results.Json(new AppVersionResponseDto(version.Current.Version, version.Current.Channel)));

    // Issue #249 (epic #248): operator-console update status + on-demand check.
    app.MapUpdatesApi();

    // Epic #271 / #312 Phase 1: the registered check providers + config schemas, for
    // the schema-driven edit dialog.
    app.MapCheckProviderApi();

    // #343 Phase 2: the registered notification channels (delivery sibling of the check
    // providers), for the Plugins page's "Notification channels" catalogue.
    app.MapNotificationProviderApi();

    // #365: operator-only per-channel test send (generic over any SupportsTest channel).
    app.MapNotificationChannelTestApi();

    // Issue #291 Phase A: linked webhook / alert-profile admin CRUD + backfill preview.
    app.MapLinkedTargetsAdminApi();

    // Issue #293 Phase A: SLA admin CRUD + backfill preview.
    app.MapSlaAdminApi();

    // Public, versioned, machine-readable status endpoint (issue #108).
    // Contract documented in docs/api.md.
    app.MapPublicStatusApi();
}

// Issue #105. PATCH /statuscheck/{id}/enabled body shape.
public sealed record EnabledRequest(bool Enabled);

// #320 Phase 2b. Operator-only heartbeat-token payload (GET/regenerate). The token is a
// credential, so it only flows over the authenticated /statuscheck/{id}/heartbeat path —
// never the anonymous read VM. The Web app composes the public ping URL from it.
public sealed record HeartbeatTokenResponse(string Token);

// Issue #241 Phase B. POST /admin/email/test body — optional recipient override.
public sealed record EmailTestRequest(string? To);

// Issue #241 Phase C. POST /api/push/subscribe body — a browser PushSubscription.
public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string? UserAgent);

// Issue #241 Phase C. POST /api/push/unsubscribe body — the endpoint to drop.
public sealed record PushUnsubscribeRequest(string Endpoint);

// Issue #136. Short-TTL, single-flight cache for the heavy dashboard summary.
// Extracted so both "a cache hit recomputes nothing" and "concurrent cold-cache
// callers compute once" are unit-testable.
public static class DashboardCache
{
    public const string SummaryKey = "dashboard-summary";
    public const int TtlSeconds = 20;

    // One global key (the summary is instance-wide), so a single gate coalesces
    // all in-flight computation. IMemoryCache.GetOrCreateAsync is NOT
    // single-flight — concurrent cold/expired callers would all run the factory
    // (Hermes review on #136), defeating the point on a dashboard refresh storm.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Return the cached summary, or run <paramref name="factory"/>
    /// EXACTLY ONCE across concurrent callers and cache it for
    /// <see cref="TtlSeconds"/>. The factory computes the WHOLE payload (incl.
    /// incident count), so a hit skips all of it.</summary>
    public static async Task<DashboardSummaryViewModel?> GetOrComputeAsync(IMemoryCache cache, Func<Task<DashboardSummaryViewModel>> factory)
    {
        if (cache.TryGetValue(SummaryKey, out DashboardSummaryViewModel? cached))
        {
            return cached;
        }
        await _gate.WaitAsync();
        try
        {
            // Double-check: a caller that was waiting on the gate while the
            // first one computed just needs the now-cached value.
            if (cache.TryGetValue(SummaryKey, out cached))
            {
                return cached;
            }
            DashboardSummaryViewModel result = await factory();
            cache.Set(SummaryKey, result, TimeSpan.FromSeconds(TtlSeconds));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
