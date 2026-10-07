using SuperStatus.Data.Constants;
using SuperStatus.Data.DTO;
using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents a failed status check at that time
    /// </summary>
    public class HistoricalStatusData : EntityBase
    {
        public HistoricalStatusData()
        {
        }
        public HistoricalStatusData(StatusCheckResult statusCheckResult, FailType failType)
        {
            StatusCheck = statusCheckResult.StatusCheck;
            HttpStatusCode = statusCheckResult.HttpStatusCode;
            ResponseTimeInMs = statusCheckResult.ResponseTimeInMs;
            TimeOfCheckUTC = DateTime.UtcNow;
            CheckFailed = statusCheckResult.CheckFailed;
            HistoricalStatusAction = null;
            FailType = failType;
        }
        [ForeignKey("StatusCheck")]
        public long StatusCheckId { get; set; }
        public StatusCheck StatusCheck { get; set; }
        public int HttpStatusCode { get; set; }
        public long ResponseTimeInMs { get; set; }
        public DateTime TimeOfCheckUTC { get; set; }
        public bool CheckFailed { get; set; }
        public FailType FailType { get; set; }

        // Epic #271 / #312 Phase 1: optional declared-metrics payload emitted by a
        // provider for this tick. Null for HTTP / all of Phase 1 (no provider emits
        // metrics yet and nothing reads this); the typed MetricDefs + retention/query
        // semantics land in Phase 2.
        public string? MetricsJson { get; set; }

        public HistoricalStatusAction? HistoricalStatusAction { get; set; }
    }
}
