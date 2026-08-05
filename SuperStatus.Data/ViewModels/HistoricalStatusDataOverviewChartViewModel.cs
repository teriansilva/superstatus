using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class HistoricalStatusDataOverviewChartViewModel
    {
        public HistoricalStatusDataOverviewChartViewModel(long statusCheckId, DateOnly date, int failedResponseCount, int slowResponseCount, int unreachableCount, int total = 0)
        {
            StatusCheckId = statusCheckId;
            Date = date;
            FailedResponseCount = failedResponseCount;
            SlowResponseCount = slowResponseCount;
            UnreachableCount = unreachableCount;
            Total = total;
        }
        public long StatusCheckId { get; set; }
        public DateOnly Date { get; set; }
        public int FailedResponseCount { get; set; }
        public int SlowResponseCount { get; set; }
        public int UnreachableCount { get; set; }

        /// <summary>Issue #200: total samples recorded that day. Lets the strip tell a
        /// no-data day (Total == 0 → grey "gap") apart from a perfectly-healthy day
        /// (Total &gt; 0, no failures → green) — both otherwise have all-zero failure
        /// counts. Defaults to 0 so a no-data day is never painted green.</summary>
        public int Total { get; set; }

        public bool FailedStatus => FailedResponseCount > 0;
        public bool SlowResponse => SlowResponseCount > 0;
        public bool Unreachable => UnreachableCount > 0;
        public bool NoFailures => FailedResponseCount == 0 && SlowResponseCount == 0 && UnreachableCount == 0;

        /// <summary>Issue #200: whether the day recorded any samples at all.</summary>
        public bool HasData => Total > 0;

    }

}
