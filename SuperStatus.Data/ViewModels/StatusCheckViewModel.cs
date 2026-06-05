using Microsoft.Extensions.Primitives;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class StatusCheckViewModel : StatusCheckViewModelBase
    {
        public StatusCheckViewModel() 
        {
            Title = string.Empty;
            StatusCheckUrl = string.Empty;
            WebHookOnErrorUrl = string.Empty;
            ServiceLogoUrl = string.Empty;
        }
        public StatusCheckViewModel(StatusCheck statusCheck, HistoricalStatusDataViewModel? mostRecentHistoricalStatusData)
        {
            Id = statusCheck.Id;
            Title = statusCheck.Title;
            StatusCheckUrl = statusCheck.StatusCheckUrl;
            IsWebHookOnErrorEnabled = statusCheck.IsWebHookOnErrorEnabled;
            WebHookOnErrorUrl = statusCheck.WebHookOnErrorUrl;
            ExpectedStatusCode = statusCheck.ExpectedStatusCode;
            ExpectedResponseTimeInMs = statusCheck.ExpectedResponseTimeInMs;
            Description = statusCheck.Description;
            Enabled = statusCheck.Enabled;
            ServiceLogoUrl = statusCheck.ServiceLogoUrl;
            IntervalSeconds = statusCheck.IntervalSeconds;
            ThrottleWebHookToExecuteOnlyEveryXMinutes = statusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes;
            ConsecutiveFailures = statusCheck.ConsecutiveFailures;
            MostRecentHistoricalStatusCheck = mostRecentHistoricalStatusData;
        }
        public HistoricalStatusDataViewModel? MostRecentHistoricalStatusCheck { get; set; }
    }
}