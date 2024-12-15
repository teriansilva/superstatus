using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class HistoricalStatusDataOverviewChartViewModel
    {
        public HistoricalStatusDataOverviewChartViewModel(long statusCheckId, DateOnly date, int failedResponseCount, int slowResponseCount, int unreachableCount)
        {
            StatusCheckId = statusCheckId;
            Date = date;
            FailedResponseCount = failedResponseCount;
            SlowResponseCount = slowResponseCount;
            UnreachableCount = unreachableCount;
        }
        public long StatusCheckId { get; set; }
        public DateOnly Date { get; set; }
        public int FailedResponseCount { get; set; }
        public int SlowResponseCount { get; set; }
        public int UnreachableCount { get; set; }
        public int OverallCount { get; set; }
        public int SuccessfulCount => OverallCount - FailedResponseCount - SlowResponseCount - UnreachableCount;
        public bool FailedStatus => FailedResponseCount > 0;
        public bool SlowResponse => SlowResponseCount > 0;
        public bool Unreachable => UnreachableCount > 0;
    }

}
