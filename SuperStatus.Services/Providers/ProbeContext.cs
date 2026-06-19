namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. Everything a provider needs to run one probe, and
    /// nothing more: the check's identity, its validated <c>ConfigJson</c>, and a hard
    /// per-probe <see cref="Timeout"/>. Deliberately does <b>not</b> expose the
    /// <c>StatusCheck</c> entity or the DB — a provider answers only "how do I probe
    /// this target", never reads or writes app state.
    /// </summary>
    public sealed class ProbeContext
    {
        public ProbeContext(long checkId, string checkTitle, string configJson, TimeSpan timeout, DateTime? lastSignalUtc = null)
        {
            CheckId = checkId;
            CheckTitle = checkTitle;
            ConfigJson = configJson;
            Timeout = timeout;
            LastSignalUtc = lastSignalUtc;
        }

        /// <summary>The check's id (for log correlation only).</summary>
        public long CheckId { get; }

        /// <summary>The check's title (for log correlation only).</summary>
        public string CheckTitle { get; }

        /// <summary>The provider's typed config, serialized — already validated against
        /// the provider's <see cref="ConfigSchema"/> before the probe is dispatched.</summary>
        public string ConfigJson { get; }

        /// <summary>Hard per-probe ceiling. The engine also wraps the probe in a
        /// try/catch so a provider that ignores this can't hang the scheduler.</summary>
        public TimeSpan Timeout { get; }

        /// <summary>#320: for <b>push</b> providers (heartbeat / dead-man's-switch), the
        /// UTC of the last inbound signal the engine recorded for this check — so the
        /// provider classifies freshness without reading app state. Null for pull
        /// providers (http/ai).</summary>
        public DateTime? LastSignalUtc { get; }
    }
}
