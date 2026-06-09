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
            AutoIncidentEnabled = statusCheck.AutoIncidentEnabled;
            // #253: carry the per-check alert rules onto the read/edit-existing path
            // too (this ctor does not call base, so map them here as well).
            AlertOnFailureThreshold = statusCheck.AlertOnFailureThreshold;
            AlertOnOutageMinutes = statusCheck.AlertOnOutageMinutes;
            AlertOnRecovery = statusCheck.AlertOnRecovery;
            AlertThrottleMinutes = statusCheck.AlertThrottleMinutes;
            EmailAlertsEnabled = statusCheck.EmailAlertsEnabled;
            EmailRecipients = statusCheck.EmailRecipients;
            WebPushAlertsEnabled = statusCheck.WebPushAlertsEnabled;
            MostRecentHistoricalStatusCheck = mostRecentHistoricalStatusData;
        }
        public HistoricalStatusDataViewModel? MostRecentHistoricalStatusCheck { get; set; }
    }
}