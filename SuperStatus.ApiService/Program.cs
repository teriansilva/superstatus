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
using SuperStatus.Data.ViewModels;
using SuperStatus.Scheduler;
using SuperStatus.Services;
using SuperStatus.Services.Services;
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
// so the Grid view has visible buildings on staging without an operator
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
        var idpPublic =
            Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP") ?? idpInternal;

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

    app.MapGet("/statuscheck/grid", async (IStatusCheckService statusCheckService) =>
    {
        // Per-check snapshot consumed by the Grid renderer (issue #11).
        // Anonymous read, mirrors /statuscheck policy: empty collection is a
        // valid 200, not a missing resource.
        List<GridBuildingViewModel> buildings = await statusCheckService.GetGridBuildings();
        return Results.Ok(buildings);
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

    app.MapPost("/statuscheck/edit", async (StatusCheckViewModelBase statusCheckToUpdate, IStatusCheckService statusCheckService) =>
    {
        await statusCheckService.AddOrUpdateStatusCheck(statusCheckToUpdate);
        return Results.Ok();
    }).RequireAuthorization();

    // Issue #105. Operator-only manual run.
    app.MapPost("/statuscheck/{id:long}/run-now", async (long id, IStatusCheckService statusCheckService, CancellationToken ct) =>
    {
        StatusCheck? check = await statusCheckService.GetStatusCheck(id);
        if (check is null) return Results.NotFound();
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

    // Public, versioned, machine-readable status endpoint (issue #108).
    // Contract documented in docs/api.md.
    app.MapPublicStatusApi();
}

// Issue #105. PATCH /statuscheck/{id}/enabled body shape.
public sealed record EnabledRequest(bool Enabled);

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

