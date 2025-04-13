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

        private static IConfigurationRoot configuration;

        static SuperStatusConfig()
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), @"..\", "SuperStatus.Configuration", SuperStatusConfigFileName);
            configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), @"..\", "SuperStatus.Configuration", SuperStatusConfigFileName), optional: false, reloadOnChange: true)
            .Build();
        }

        public static string GetValue(string key)
        {
            return configuration.GetSection(key)?.Value ?? string.Empty;
        }

    }
}
