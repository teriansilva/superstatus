using Microsoft.Extensions.Configuration;
using SuperStatus.Data.Entities;

namespace SuperStatus.Configuration
{
    public static class SuperStatusConfig
    {

        public static string SuperStatusConfigFileName = "SuperStatus.config.json";

        private static string _Title = "SuperStatusConfig:Title";
        private static string _Description = "SuperStatusConfig:Description";
        private static string _LogoUrl = "SuperStatusConfig:LogoUrl";
        private static string _Favicon = "SuperStatusConfig:Favicon";
        private static string _Theme = "SuperStatusConfig:Theme";
        private static string _ShowSupportLink = "SuperStatusConfig:ShowSupportLink";
        private static string _JobIntervallInSeconds = "SuperStatusConfig:JobIntervallInSeconds";
        private static string _DbCleanUpJobIntervallInMinutes = "SuperStatusConfig:DbCleanUpJobIntervallInMinutes";
        private static string _JobName = "SuperStatusConfig:JobName";
        private static string _RunJobAtStartup = "SuperStatusConfig:RunJobAtStartup";
        private static string _StatusCheckViewRefreshIntervalInSeconds = "SuperStatusConfig:StatusCheckViewRefreshIntervalInSeconds";
        private static string _StatusCheckGraphViewMaxDays = "SuperStatusConfig:StatusCheckGraphViewMaxDays";
        private static string _ShowSlowResponseTimeInGraph = "SuperStatusConfig:ShowSlowResponseTimeInGraph";
        private static string _MaxConcurrentChecks = "SuperStatusConfig:MaxConcurrentChecks";
        private static string _RawTickRetentionHours = "SuperStatusConfig:RawTickRetentionHours";

        public static string Title => GetValue(_Title);
        public static string Description => GetValue(_Description);
        public static string LogoUrl => GetValue(_LogoUrl);
        public static string Favicon => GetValue(_Favicon);
        public static string Theme => GetValue(_Theme);
        public static string ShowSupportLink => GetValue(_ShowSupportLink);
        public static int JobIntervallInSeconds => int.Parse(GetValue(_JobIntervallInSeconds));
        public static int DbCleanUpJobIntervallInMinutes => int.Parse(GetValue(_DbCleanUpJobIntervallInMinutes));
        public static string JobName => GetValue(_JobName);
        public static bool RunJobAtStartup => GetValue(_RunJobAtStartup).ToLower().Equals("true");
        public static int StatusCheckViewRefreshIntervalInSeconds => int.Parse(GetValue(_StatusCheckViewRefreshIntervalInSeconds));
        public static int StatusCheckGraphViewMaxDays => int.Parse(GetValue(_StatusCheckGraphViewMaxDays));
        public static bool ShowSlowResponseTimeInGraph => GetValue(_ShowSlowResponseTimeInGraph).ToLower().Equals("true");

        /// <summary>
        /// Issue #78. Max checks executed concurrently within one scheduler
        /// tick. Defaults to 8 when missing/unparseable and is clamped to ≥1 —
        /// 8 leaves headroom under the Npgsql pool (Maximum Pool Size=20) for
        /// the API and Identity. Tune down for tiny installs, up only
        /// with the pool size.
        /// </summary>
        public static int MaxConcurrentChecks
        {
            get => int.TryParse(GetValue(_MaxConcurrentChecks), out int v) ? Math.Max(1, v) : 8;
        }

        /// <summary>
        /// Issue #138 (P1). How many hours of raw <c>HistoricalStatusData</c> ticks
        /// to keep at full resolution — the raw-tick prune cutoff (PR-C). Distinct
        /// from <see cref="StatusCheckGraphViewMaxDays"/> (the rollup/webhook-log
        /// retention window): the small-footprint model keeps ~3 days of raw but
        /// 30 days of daily rollups. The dashboard read path only aggregates TODAY
        /// on-the-fly (prior days come from the rollup table), so this is the prune
        /// floor, not the read boundary; clamped to ≥ 48 h so today's raw is always
        /// retained and the rollup job's recent-day refresh always has raw to read.
        /// </summary>
        public static int RawTickRetentionHours
        {
            get => int.TryParse(GetValue(_RawTickRetentionHours), out int v) ? Math.Max(48, v) : 72;
        }

        private static IConfigurationRoot configuration;

        static SuperStatusConfig()
        {
            // The .csproj copies SuperStatus.config.json next to the published
            // assemblies via CopyToPublishDirectory + CopyToOutputDirectory, so
            // anchor on AppContext.BaseDirectory rather than CurrentDirectory.
            // The previous "..\SuperStatus.Configuration\<file>" path only
            // resolved when the process was launched from the project source
            // folder (e.g. dotnet run from <repo>/SuperStatus.ApiService),
            // and broke under `dotnet SuperStatus.ApiService.dll` from /app
            // inside the container.
            var configPath = Path.Combine(AppContext.BaseDirectory, SuperStatusConfigFileName);
            configuration = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: false, reloadOnChange: true)
                .Build();
        }

        public static string GetValue(string key)
        {
            return configuration.GetSection(key)?.Value ?? string.Empty;
        }

    }
}
