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
            ServiceLogoUrl = string.Empty;
        }
        public StatusCheckViewModel(StatusCheck statusCheck, HistoricalStatusDataViewModel? mostRecentHistoricalStatusData)
        {
            Id = statusCheck.Id;
            Title = statusCheck.Title;
            StatusCheckUrl = statusCheck.StatusCheckUrl;
            ExpectedStatusCode = statusCheck.ExpectedStatusCode;
            // #293: effective slow threshold from the linked SLA (this ctor
            // does not call base, so map it here as well — see base ctor note).
            EffectiveSlowThresholdMs = statusCheck.Sla?.SlowThresholdMs ?? 0;
            LinkedSlaId = statusCheck.SlaId;
            LinkedSlaName = statusCheck.Sla?.Name;
            SlaTargetUptimePercent = statusCheck.Sla?.TargetUptimePercent ?? 100;
            SlaCriticalUptimePercent = statusCheck.Sla?.CriticalUptimePercent ?? 100;
            Description = statusCheck.Description;
            Enabled = statusCheck.Enabled;
            ServiceLogoUrl = statusCheck.ServiceLogoUrl;
            // #312: provider type + read-side config (this ctor does not call base).
            ProviderType = string.IsNullOrWhiteSpace(statusCheck.ProviderType) ? "http" : statusCheck.ProviderType;
            if (ProviderType == "http")
            {
                ProviderConfig["url"] = statusCheck.StatusCheckUrl ?? string.Empty;
                ProviderConfig["expectedStatusCode"] = statusCheck.ExpectedStatusCode.ToString();
            }
            IntervalSeconds = statusCheck.IntervalSeconds;
            ConsecutiveFailures = statusCheck.ConsecutiveFailures;
            AutoIncidentEnabled = statusCheck.AutoIncidentEnabled;
            // #253: carry the per-check alert rules onto the read/edit-existing path
            // too (this ctor does not call base, so map them here as well).
            AlertOnFailureThreshold = statusCheck.AlertOnFailureThreshold;
            AlertOnOutageMinutes = statusCheck.AlertOnOutageMinutes;
            AlertOnRecovery = statusCheck.AlertOnRecovery;
            AlertThrottleMinutes = statusCheck.AlertThrottleMinutes;
            MostRecentHistoricalStatusCheck = mostRecentHistoricalStatusData;
        }
        public HistoricalStatusDataViewModel? MostRecentHistoricalStatusCheck { get; set; }
    }
}
