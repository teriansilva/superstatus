namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. A pluggable check type. A provider answers exactly two
    /// questions: <b>how do I probe this target</b> (<see cref="ProbeAsync"/>) and
    /// <b>what does its config look like</b> (<see cref="Descriptor"/>). It owns no app
    /// state — the engine handles scheduling, history, incidents, alerting, and the
    /// cross-cutting latency SLO.
    /// <para>
    /// <b>Trust boundary (see <c>docs/check-providers.md</c>):</b> in-process providers
    /// are <b>trusted, first-party-reviewed C#</b> running with full process access.
    /// Untrusted / community code is out of scope until the later out-of-process
    /// protocol (epic #271 Phase 4) and must never be loaded in-process.
    /// </para>
    /// </summary>
    public interface ICheckProvider
    {
        /// <summary>Static description: id, display name, icon, versioned config schema,
        /// declared metrics (empty in Phase 1).</summary>
        ProviderDescriptor Descriptor { get; }

        /// <summary>
        /// Probe the target once and return a normalized <see cref="ProbeResult"/>. The
        /// provider should classify its own protocol-level outcome (reachable? expected
        /// response?) and report latency. It must <b>not</b> apply the latency SLO — the
        /// engine does that. Implementations should be defensive, but the engine also
        /// wraps every call in a timeout + try/catch, so a throw or hang is converted to
        /// a normalized <c>down</c>/<see cref="Data.Constants.FailType.Unreachable"/>
        /// result and never reaches the scheduler.
        /// </summary>
        Task<ProbeResult> ProbeAsync(ProbeContext context, CancellationToken cancellationToken = default);
    }
}
