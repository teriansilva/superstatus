using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class HistoricalStatusDataViewModel
    {
        public HistoricalStatusDataViewModel(HistoricalStatusData historicalStatusData, FailType failType)
        {
            StatusCheckId = historicalStatusData.StatusCheckId;
            HttpStatusCode = historicalStatusData.HttpStatusCode;
            ResponseTimeInMs = historicalStatusData.ResponseTimeInMs;
            TimeOfCheckUTC = historicalStatusData.TimeOfCheckUTC;
            CheckFailed = historicalStatusData.CheckFailed;
            FailType = failType;
        }

        public long StatusCheckId { get; set; }
        public int HttpStatusCode { get; set; }
        public long ResponseTimeInMs { get; set; }
        public DateTime TimeOfCheckUTC { get; set; }
        public bool CheckFailed { get; set; }
        public FailType FailType { get; set; }
    }

}
