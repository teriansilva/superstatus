using SuperStatus.Data.DatabaseContext;
using SuperStatus.Services.Services;


namespace SuperStatus.Services
{
    /// <summary>
    /// Initializes all the require application services
    /// </summary>
    public static class ServiceRegistration
    {
        public static void AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
        {

            services.AddSuperStatusDb(configuration);
            services.AddRepositories();

            // Issue #77: named, pooled, instrumented clients for the status-check
            // pipeline (replaces raw new HttpClient()). Implicitly registers
            // IHttpClientFactory for StatusCheckService.
            services.AddStatusCheckHttpClients();

            services.AddScoped<IStatusCheckService, StatusCheckService>();
            services.AddScoped<IIncidentService, IncidentService>();
            services.AddScoped<ISiteSettingsService, SiteSettingsService>();

            // Issue #168: AI-authored incidents on sustained downtime. The queue is a
            // process singleton (the scheduler enqueues, the background worker drains);
            // the draft service + coordinator are scoped (per check / per worker item).
            services.AddSingleton<IAutoIncidentQueue, AutoIncidentQueue>();
            services.AddScoped<IIncidentDraftService, IncidentDraftService>();
            services.AddScoped<IAutoIncidentCoordinator, AutoIncidentCoordinator>();

        }
    }
}
