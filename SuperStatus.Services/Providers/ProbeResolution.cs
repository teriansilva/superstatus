namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. The result of resolving a check to a runnable probe:
    /// either a usable (provider + validated config) pair, or a calm
    /// <see cref="DisableReason"/>. This is the <b>single</b> gate both the scheduled
    /// tick and the manual run-now path consult — so unknown / missing / invalid config
    /// disables a check identically everywhere (never a crash, never a silent default
    /// probe).
    /// </summary>
    public sealed class ProbeResolution
    {
        private ProbeResolution(ICheckProvider? provider, string effectiveConfigJson, string? disableReason)
        {
            Provider = provider;
            EffectiveConfigJson = effectiveConfigJson;
            DisableReason = disableReason;
        }

        /// <summary>The resolved provider when <see cref="IsDisabled"/> is false; null otherwise.</summary>
        public ICheckProvider? Provider { get; }

        /// <summary>The validated config the probe should run with (may be backfilled from
        /// the legacy HTTP columns for a row that predates <c>ConfigJson</c>).</summary>
        public string EffectiveConfigJson { get; }

        /// <summary>A calm, human-readable reason the check is disabled, or null when runnable.</summary>
        public string? DisableReason { get; }

        public bool IsDisabled => DisableReason is not null;

        public static ProbeResolution Ok(ICheckProvider provider, string effectiveConfigJson)
            => new(provider, effectiveConfigJson, null);

        public static ProbeResolution Disabled(string reason)
            => new(null, string.Empty, reason);
    }
}
