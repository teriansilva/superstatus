namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents application configuration stored in the database
    /// </summary>
    public class Configuration : EntityBase
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string LogoUrl { get; set; } = string.Empty;
        public string Favicon { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public bool ShowSupportLink { get; set; }
        public int JobIntervallInSeconds { get; set; }
        public int DbCleanUpJobIntervallInMinutes { get; set; }
        public string JobName { get; set; } = string.Empty;
        public bool RunJobAtStartup { get; set; }
        public int StatusCheckViewRefreshIntervalInSeconds { get; set; }
        public int StatusCheckGraphViewMaxDays { get; set; }
        public bool ShowSlowResponseTimeInGraph { get; set; }
    }
}