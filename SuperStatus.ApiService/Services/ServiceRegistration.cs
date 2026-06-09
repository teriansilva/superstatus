using SuperStatus.Data.DatabaseContext;
using SuperStatus.Services.Services;
using SuperStatus.Services.Updates;


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

            // Issue #249 (epic #248): reports the running app's version (read once
            // from the stamped assembly) for GET /api/version + update detection.
            services.AddSingleton<IAppVersionProvider, AppVersionProvider>();

            // Issue #249: GitHub Releases update check. Unauthenticated public API —
            // GitHub requires a User-Agent; a short timeout keeps a slow/blocked call
            // from holding the nightly cycle. The service is error-tolerant.
            services.AddHttpClient(GitHubUpdateCheckService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SuperStatus-UpdateCheck");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            });
            services.AddScoped<IUpdateCheckService, GitHubUpdateCheckService>();

            // Issue #168: AI-authored incidents on sustained downtime. The queue is a
            // process singleton (the scheduler enqueues, the background worker drains);
            // the draft service + coordinator are scoped (per check / per worker item).
            services.AddSingleton<IAutoIncidentQueue, AutoIncidentQueue>();
            services.AddScoped<IIncidentDraftService, IncidentDraftService>();
            services.AddScoped<IAutoIncidentCoordinator, AutoIncidentCoordinator>();

            // Issue #241/#253: per-check alert evaluator (threshold/outage/recovery
            // → AlertDeliveryLog). Scoped — one per check tick, like the coordinator.
            services.AddScoped<SuperStatus.Services.Alerts.IAlertEvaluator, SuperStatus.Services.Alerts.AlertEvaluator>();
            // Issue #241 Phase B: SMTP email notifier (MailKit) for alert delivery.
            services.AddScoped<SuperStatus.Services.Alerts.IEmailNotifier, SuperStatus.Services.Alerts.MailKitEmailNotifier>();
            // Issue #241 Phase C: browser Web Push notifier (VAPID) for alert delivery.
            services.AddScoped<SuperStatus.Services.Alerts.IWebPushNotifier, SuperStatus.Services.Alerts.WebPushNotifier>();
            // The push sender's HTTP client (pooled handler; per-send timeout is applied
            // by the notifier's linked CTS).
            services.AddHttpClient(SuperStatus.Services.Alerts.WebPushNotifier.HttpClientName);

        }
    }
}
