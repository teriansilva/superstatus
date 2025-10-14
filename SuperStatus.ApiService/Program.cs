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
using Quartz;
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
ConfigureQuartz(builder);
WebApplication app = builder.Build();

ConfigureMiddleware(app);
await SuperStatusDbInitializer.Seed(app.Services, EnvironmentUtilities.IsDevEnvironment(app.Environment.EnvironmentName));

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
        options.Authority = Environment.GetEnvironmentVariable(
            "IDP_HTTP");
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

    services.AddHttpContextAccessor();

    services.AddProblemDetails(ConfigureProblemDetails)
        .AddProblemDetailsConventions();

    AddRateLimiter(services, applicationBuilder.Configuration);

    AddControllers(services);
    AddSwagger(services);
}

void ConfigureQuartz(WebApplicationBuilder builder)
{

    if (SuperStatusConfig.RunJobAtStartup)
    {
        builder.Services.AddQuartz(q =>
        {
            q.SchedulerId = "JobScheduler";
            q.SchedulerName = "Job Scheduler";
            q.AddJob<SuperStatusCheckJob>(opts => opts.WithIdentity(SuperStatusConfig.JobName));
            q.AddJob<SuperStatusCleanUpJob>(opts => opts.WithIdentity(typeof(SuperStatusCleanUpJob).Name));
            q.AddTrigger(opts => opts
                .ForJob(SuperStatusConfig.JobName)
                .WithIdentity($"{SuperStatusConfig.JobName}-interval")
                .WithDescription("Status Check default job")
                .WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(SuperStatusConfig.JobIntervallInSeconds)
                    .RepeatForever())
            );
            q.AddTrigger(opts => opts
                .ForJob(typeof(SuperStatusCleanUpJob).Name)
                .WithIdentity($"{typeof(SuperStatusCleanUpJob).Name}-interval")
                .WithDescription("Status Check db cleanup job")
                .WithSimpleSchedule(x => x
                    .WithIntervalInMinutes(SuperStatusConfig.DbCleanUpJobIntervallInMinutes)
                    .RepeatForever())
            );
        });

        builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
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
                "Too many requests — please try again later.", cancellationToken);
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
        IPagedResult<StatusCheckViewModel> statusCheck = await statusCheckService.GetStatusCheckViewModelSet();
        if (statusCheck.RowCount == 0)
        {
            return Results.NotFound("No status checks found.");
        }
        return Results.Ok(statusCheck);
    });

    app.MapGet("/statuscheck/gethistoricaldata/{id}", async (int id, IStatusCheckService statusCheckService) =>
    {
        return await statusCheckService.GetHistoricalStatusDataOverviewForRecentTimeRange(id, SuperStatusConfig.StatusCheckGraphViewMaxDays);
    });

    app.MapPost("/statuscheck/edit", async (StatusCheckViewModelBase statusCheckToUpdate, IStatusCheckService statusCheckService) =>
    {
        await statusCheckService.AddOrUpdateStatusCheck(statusCheckToUpdate);
        return Results.Ok();
    }).RequireAuthorization();


    app.MapGet("/incidents", async (IIncidentService incidentService) =>
    {
        IDictionary<DateTime, List<IncidentViewModel>> incidents = await incidentService.GetIncidentViewModelSetForDays();
        if (incidents.Count == 0)
        {
            return Results.NotFound("No incidents found.");
        }
        return Results.Ok(incidents);
    });
}

