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
        public HistoricalStatusAction? HistoricalStatusAction { get; set; }
    }
}
