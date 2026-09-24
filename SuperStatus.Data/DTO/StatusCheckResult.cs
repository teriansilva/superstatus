using SuperStatus.Data.Entities;

namespace SuperStatus.Data.DTO
{
    public class StatusCheckResult
    {
        public StatusCheckResult(StatusCheck statusCheck, long responseTimeInMs, int httpStatusCode, bool checkFailed)
        {
            StatusCheck = statusCheck;
            ResponseTimeInMs = responseTimeInMs;
            HttpStatusCode = httpStatusCode;
            TimeOfCheckUTC = DateTime.UtcNow;
            CheckFailed = checkFailed;

        }

        public StatusCheck StatusCheck { get; set; }
        public long ResponseTimeInMs { get; set; }
        public int HttpStatusCode { get; set; }
        public DateTime TimeOfCheckUTC { get; set; }
        public bool CheckFailed { get; set; }

    }
}
