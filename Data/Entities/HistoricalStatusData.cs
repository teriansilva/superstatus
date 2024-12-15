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
        public HistoricalStatusData(StatusCheckResult statusCheckResult)
        {
            StatusCheckId = statusCheckResult.StatusCheck.Id;
            HttpStatusCode = statusCheckResult.HttpStatusCode;
            ResponseTimeInMs = statusCheckResult.ResponseTimeInMs;
            TimeOfCheckUTC = DateTime.UtcNow;
            CheckFailed = statusCheckResult.CheckFailed;
            HistoricalStatusAction = null;

        }
        //[ForeignKey("StatusCheck")]
        public long StatusCheckId { get; set; }
        //public virtual StatusCheck StatusCheck { get; set; }
        public int HttpStatusCode { get; set; }
        public long ResponseTimeInMs { get; set; }
        public DateTime TimeOfCheckUTC { get; set; }
        public bool CheckFailed { get; set; }

        public virtual HistoricalStatusAction? HistoricalStatusAction { get; set; }
    }
}
