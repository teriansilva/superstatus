using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Data.DatabaseContext;

/// <summary>
/// Extension methods for registering the SuperTalk Db Context in the service collection.
/// </summary>
public static class SuperStatusDbRegistrations
{
    /// <summary>
    /// Registers the SuperTalk Db Context to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration settings for the application.</param>
    public static void AddSuperStatusDb(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("SuperStatusDb");
        services.AddDbContext<SuperStatusDb>(options =>
            options.UseNpgsql(cs));
    }

    /// <summary>
    /// Registers the repositories in the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddRepositories(this IServiceCollection services)
    {
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IStatusCheckRepository, StatusCheckRepository>();
        services.AddScoped<IHistoricalStatusDataRepository, HistoricalStatusDataRepository>();
        services.AddScoped<IHistoricalStatusActionRepository, HistoricalStatusActionRepository>();
        services.AddScoped<IWebhookExecutionLogRepository, WebhookExecutionLogRepository>();
        services.AddScoped<IAlertDeliveryLogRepository, AlertDeliveryLogRepository>();
        services.AddScoped<IIncidentRepository, IncidentRepository>();
        services.AddScoped<IDailyStatusRollupRepository, DailyStatusRollupRepository>();
        services.AddScoped<ISiteSettingsRepository, SiteSettingsRepository>();
        services.AddScoped<IPushSubscriptionRepository, PushSubscriptionRepository>();
        // #291: link-table persistence for linked webhooks / alert profiles.
        services.AddScoped<IStatusCheckLinkRepository, StatusCheckLinkRepository>();
    }
}