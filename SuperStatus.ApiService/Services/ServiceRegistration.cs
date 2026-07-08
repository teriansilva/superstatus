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

            // Epic #271 / #312 Phase 1: the in-process check-provider registry. Providers
            // are trusted, first-party C# (the only Phase-1 provider is HTTP). Singletons —
            // they're stateless and depend only on the singleton IHttpClientFactory.
            services.AddSingleton<SuperStatus.Services.Providers.ICheckProvider, SuperStatus.Services.Providers.Http.HttpCheckProvider>();
            // #317 Phase 2a: the AI/LLM canary provider (metric-emitting).
            services.AddSingleton<SuperStatus.Services.Providers.ICheckProvider, SuperStatus.Services.Providers.Ai.AiCheckProvider>();
            // #320 Phase 2b: the agent-heartbeat (push / dead-man's-switch) provider —
            // DELIBERATELY NOT REGISTERED (operator decision, 2026-07-07): parked until
            // there's real demand; http + ai cover current needs. The provider class,
            // its tests, and the (registry-gated) ping endpoints all remain, so
            // re-enabling is exactly this one registration line:
            // services.AddSingleton<SuperStatus.Services.Providers.ICheckProvider, SuperStatus.Services.Providers.Heartbeat.HeartbeatCheckProvider>();
            // Existing heartbeat checks (if any) are disabled calmly by the #312
            // unknown-type gate and surface as "not registered" on /plugins.
            services.AddSingleton<SuperStatus.Services.Providers.ICheckProviderRegistry, SuperStatus.Services.Providers.CheckProviderRegistry>();

            services.AddScoped<IStatusCheckService, StatusCheckService>();
            // #291: the single legacy-fields ↔ linked-targets translation path
            // (edit endpoint, startup backfill, admin preview all go through it).
            services.AddScoped<ILinkedTargetNormalizationService, LinkedTargetNormalizationService>();
            // #293: the single legacy-ms ↔ linked-SLA translation path (same pattern).
            services.AddScoped<ISlaNormalizationService, SlaNormalizationService>();
            // #342: batch add — create many checks from a pasted target list in one
            // transaction, reusing the single-create path per target.
            services.AddScoped<IBatchCheckCreationService, BatchCheckCreationService>();
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

            // Issue #311: in-app "Update now" → Watchtower's authenticated http-api
            // (/v1/update). The app never touches the Docker socket; it just POSTs the
            // trigger. The URL + token come from the api env, set only by the opt-in
            // Watchtower overlay; absent ⇒ CanApply is false ⇒ the console offers the
            // guided command instead. Singleton so the anti-spam cooldown is process-wide;
            // the short timeout means we await only Watchtower's accept/reject, not the update.
            services.AddSingleton(UpdateTriggerOptions.FromEnvironment());
            services.AddHttpClient(WatchtowerUpdateTrigger.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
            });
            services.AddSingleton<IUpdateTrigger, WatchtowerUpdateTrigger>();

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
            // Still registered directly — the admin /admin/email/test endpoint uses it,
            // and the email channel provider (#343) wraps it.
            services.AddScoped<SuperStatus.Services.Alerts.IEmailNotifier, SuperStatus.Services.Alerts.MailKitEmailNotifier>();
            // Issue #241 Phase C: browser Web Push notifier (VAPID) for alert delivery.
            services.AddScoped<SuperStatus.Services.Alerts.IWebPushNotifier, SuperStatus.Services.Alerts.WebPushNotifier>();

            // #343 Phase 1: the notification-provider seam — pluggable delivery channels,
            // the delivery sibling of the check-provider registry. Scoped because each
            // channel wraps the scoped notifier + repositories. AlertEvaluator now routes
            // through the registry instead of the concrete-notifier switch.
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProvider, SuperStatus.Services.Notifications.EmailNotificationProvider>();
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProvider, SuperStatus.Services.Notifications.WebPushNotificationProvider>();
            // #343 Phase 4: the outgoing-webhook channel (POSTs a JSON payload). Uses the
            // existing named "status-webhook" HttpClient registered by AddStatusCheckHttpClients.
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProvider, SuperStatus.Services.Notifications.WebhookNotificationProvider>();
            // #343 Phase 5: chat channels — Slack + Discord (incoming webhooks) and Telegram
            // (Bot API). All reuse the same named "status-webhook" HttpClient as the webhook channel.
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProvider, SuperStatus.Services.Notifications.SlackNotificationProvider>();
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProvider, SuperStatus.Services.Notifications.DiscordNotificationProvider>();
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProvider, SuperStatus.Services.Notifications.TelegramNotificationProvider>();
            services.AddScoped<SuperStatus.Services.Notifications.INotificationProviderRegistry, SuperStatus.Services.Notifications.NotificationProviderRegistry>();
            // The push sender's HTTP client (pooled handler; per-send timeout is applied
            // by the notifier's linked CTS).
            services.AddHttpClient(SuperStatus.Services.Alerts.WebPushNotifier.HttpClientName);

        }
    }
}
