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
        public bool FailedStatus => FailedResponseCount > 0;
        public bool SlowResponse => SlowResponseCount > 0;
        public bool Unreachable => UnreachableCount > 0;
        public bool NoFailures => FailedResponseCount == 0 && SlowResponseCount == 0 && UnreachableCount == 0;
    }

}
