using SuperStatus.Data.Constants;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents an incident report that can be created by the system or manually by a user
    /// </summary>
    public class Incident : EntityBase
    {
        public bool AuotmaticallyGeneratedReport { get; set; }
        public string Title { get; set; } = string.Empty;
        public ICollection<HistoricalStatusData>? HistoricalStatusData { get; set; } = new List<HistoricalStatusData>();
        public bool Resolved { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public bool VisibleToPublic { get; set; }

        // Issue #106 — severity + resolution timestamp. Severity drives the
        // .incident.minor / .severe styling; ResolvedUtc backs MTTR. The
        // open/resolved lifecycle continues to ride on the existing
        // Resolved bool (single source of truth — a separate State enum +
        // Monitoring intermediate is deliberately deferred to avoid two
        // drifting lifecycle fields). Created is the incident start time;
        // ResolvedUtc is null until Resolved flips true.
        public IncidentSeverity Severity { get; set; } = IncidentSeverity.Minor;
        public DateTime? ResolvedUtc { get; set; }

        // Issue #168: the StatusCheck this incident was auto-drafted for (null
        // for manual incidents). Lets the sustained-downtime trigger enforce
        // "one open auto-incident per check" and auto-resolve only the linked
        // auto-incident on recovery — never a manual or unrelated one. A
        // Postgres partial unique index (SourceStatusCheckId WHERE NOT Resolved
        // AND AuotmaticallyGeneratedReport) guards against duplicates under
        // concurrent scheduler ticks.
        public long? SourceStatusCheckId { get; set; }
    }
}