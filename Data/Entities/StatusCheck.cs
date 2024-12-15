using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents a status check that should be performed.
    /// </summary>
    public class StatusCheck : EntityBase
    {
        public string Title { get; set; }
        public string StatusCheckUrl { get; set; }
        public bool IsWebHookOnErrorEnabled { get; set; }
        public string WebHookOnErrorUrl { get; set; }
        public int ThrottleWebHookToExecuteOnlyEveryXMinutes { get; set; }
        public int ExpectedStatusCode { get; set; }
        public long ExpectedResponseTimeInMs { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string ServiceLogoUrl { get; set; }
    }
}