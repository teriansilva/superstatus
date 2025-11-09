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
using SuperStatus.ApiService.Endpoints;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.Utilities;
using SuperStatus.Data.ViewModels;
using SuperStatus.Scheduler;
using SuperStatus.Services;
using SuperStatus.Services.Services;
using System.Globalization;
using System.Threading.RateLimiting;
using ProblemDetailsOptions = Hellang.Middleware.ProblemDetails.ProblemDetailsOptions;

var builder = WebApplication.CreateBuilder(args);

ConfigureServices(builder);
ConfigureReverseProxy(builder);
ConfigureAuthentication(builder);
ConfigureQuartz(builder);
WebApplication app = builder.Build();

ConfigureMiddleware(app);
await SuperStatusDbInitializer.Seed(app.Services, EnvironmentUtilities.IsDevEnvironment(app.Environment.EnvironmentName));

await ConfigureQuartzFromDatabaseAsync(app);

UseAuthentication(app);

app.MapConfigurationEndpoints();
app.MapStatusEndpoints();

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
    // Register Quartz with placeholder configuration
    // Actual jobs will be scheduled after database is seeded
    builder.Services.AddQuartz(q =>
    {
        q.SchedulerId = "JobScheduler";
        q.SchedulerName = "Job Scheduler";
    });

    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
}

static async Task ConfigureQuartzFromDatabaseAsync(WebApplication app)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var schedulerFactory = scope.ServiceProvider.GetRequiredService<ISchedulerFactory>();
        
        var config = await configRepo.GetConfigurationAsync();
        
        if (config.RunJobAtStartup)
        {
            var scheduler = await schedulerFactory.GetScheduler();
            
            // Define the status check job
            var statusCheckJob = JobBuilder.Create<SuperStatusCheckJob>()
                .WithIdentity(config.JobName)
                .Build();
            
            var statusCheckTrigger = TriggerBuilder.Create()
                .WithIdentity($"{config.JobName}-interval")
                .WithDescription("Status Check default job")
                .WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(config.JobIntervallInSeconds)
                    .RepeatForever())
                .StartNow()
                .Build();
            
            // Define the cleanup job
            var cleanupJob = JobBuilder.Create<SuperStatusCleanUpJob>()
                .WithIdentity(typeof(SuperStatusCleanUpJob).Name)
                .Build();
            
            var cleanupTrigger = TriggerBuilder.Create()
                .WithIdentity($"{typeof(SuperStatusCleanUpJob).Name}-interval")
                .WithDescription("Status Check db cleanup job")
                .WithSimpleSchedule(x => x
                    .WithIntervalInMinutes(config.DbCleanUpJobIntervallInMinutes)
                    .RepeatForever())
                .StartNow()
                .Build();
            
            // Schedule the jobs
            await scheduler.ScheduleJob(statusCheckJob, statusCheckTrigger);
            await scheduler.ScheduleJob(cleanupJob, cleanupTrigger);
            
            app.Logger.LogInformation("Quartz jobs scheduled successfully from database configuration.");
        }
        else
        {
            app.Logger.LogInformation("Jobs are disabled in configuration. Quartz scheduler started but no jobs scheduled.");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to configure Quartz from database. Jobs will not be scheduled.");
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
