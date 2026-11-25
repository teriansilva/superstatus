namespace SuperStatus.Scheduler
{
    /// <summary>
    /// Issue #78. Bounded fan-out degree for a single status-check tick.
    /// Registered as a singleton from <c>SuperStatusConfig.MaxConcurrentChecks</c>
    /// in <c>Program.cs</c>, and injected into <see cref="SuperStatusCheckJob"/>.
    /// Kept as a tiny injected record (rather than reading the static config
    /// inside the job) so the scheduler's concurrency is unit-testable without
    /// the on-disk config file.
    /// </summary>
    public sealed record SchedulerConcurrencyOptions(int MaxConcurrentChecks);
}
