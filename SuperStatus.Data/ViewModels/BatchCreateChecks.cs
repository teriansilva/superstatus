namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// Epic #342: the "Batch add" request — one provider type + a pasted target list +
    /// the shared monitoring / SLA / alerting settings applied to every created check.
    /// Posts to <c>POST /statuscheck/batch</c>. The server re-parses every target (the
    /// client parse is advisory) and creates the whole batch in one transaction.
    /// </summary>
    public sealed class BatchCreateChecksRequest
    {
        /// <summary>Provider for every check in the batch (must declare a BatchTargetField).</summary>
        public string ProviderType { get; set; } = "http";

        /// <summary>Raw pasted lines. The server also splits each entry on
        /// newlines / commas / whitespace, so a single blob or a pre-split list both work.</summary>
        public List<string> Targets { get; set; } = new();

        /// <summary>Shared provider config for the non-target fields (e.g. http
        /// <c>expectedStatusCode</c>; ai <c>model</c> / <c>prompt</c> / <c>apiKey</c> / …).
        /// The target field is filled per-target from the paste, not from here.</summary>
        public Dictionary<string, string> SharedConfig { get; set; } = new();

        public int IntervalSeconds { get; set; } = 60;
        public long? SlaId { get; set; }
        public List<long> WebhookIds { get; set; } = new();
        public List<long> AlertProfileIds { get; set; } = new();
        public int AlertOnFailureThreshold { get; set; }
        public int AlertOnOutageMinutes { get; set; }
        public int AlertThrottleMinutes { get; set; }
        public bool AlertOnRecovery { get; set; }
        public bool AutoIncidentEnabled { get; set; }
        public bool Enabled { get; set; } = true;

        /// <summary>Optional title prefix applied to every derived name.</summary>
        public string? NamePrefix { get; set; }

        /// <summary>Naming template: <c>{host}</c> (default) or <c>{host}{path}</c>.</summary>
        public string NameTemplate { get; set; } = "{host}";
    }

    /// <summary>Per-line outcome for the batch — one entry per non-blank pasted line.</summary>
    public sealed class BatchTargetResultViewModel
    {
        public string Input { get; set; } = string.Empty;
        public string? CanonicalTarget { get; set; }
        public string? Title { get; set; }
        public long? CreatedId { get; set; }

        /// <summary><c>created</c> | <c>skipped</c>.</summary>
        public string Status { get; set; } = "created";

        /// <summary>Why a line was skipped (invalid / duplicate / already-monitored).</summary>
        public string? Reason { get; set; }
    }

    /// <summary>The batch result: how many were created + the per-line breakdown.</summary>
    public sealed class BatchCreateChecksResponse
    {
        public int CreatedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<BatchTargetResultViewModel> Results { get; set; } = new();
    }
}
