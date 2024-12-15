using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class HistoricalStatusDataViewModel
    {
        public HistoricalStatusDataViewModel()
        {
            StatusCheckId = 0;
            HttpStatusCode = 0;
            ResponseTimeInMs = 0;
            TimeOfCheckUTC = DateTime.MinValue;
            CheckFailed = false;
            FailType = FailType.NoFail;
        }
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
