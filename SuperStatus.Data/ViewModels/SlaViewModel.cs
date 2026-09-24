using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>#293: SLA target + usage, for the /admin/slas surface. Embeds
    /// the same <see cref="LinkedEntitySummary"/> shape as the #291 lists
    /// (UsedByCount = checks whose SlaId references this row).</summary>
    public class SlaViewModel
    {
        public SlaViewModel() { }

        public SlaViewModel(Sla sla, List<string>? linkedCheckNames)
        {
            Id = sla.Id;
            Name = sla.Name;
            TargetUptimePercent = sla.TargetUptimePercent;
            CriticalUptimePercent = sla.CriticalUptimePercent;
            SlowThresholdMs = sla.SlowThresholdMs;
            IsDefault = sla.IsDefault;
            CreatedUtc = sla.CreatedUtc;
            Usage = LinkedEntitySummary.From(linkedCheckNames);
        }

        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double TargetUptimePercent { get; set; } = 100;
        public double CriticalUptimePercent { get; set; } = 100;
        public long SlowThresholdMs { get; set; } = 1000;

        /// <summary>Read-only on the POST surface — the default switches only
        /// through the transactional PATCH /admin/slas/{id}/default.</summary>
        public bool IsDefault { get; set; }

        public DateTime CreatedUtc { get; set; }
        public LinkedEntitySummary Usage { get; set; } = new();
    }
}
