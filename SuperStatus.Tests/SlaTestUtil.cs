using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Services;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #293 Phase A: shared wiring for tests that touch the slow-threshold
/// read. Classification resolves the threshold ONLY through the linked SLA now
/// (and throws without one), so fixtures either run the real backfill — what a
/// production upgrade does — or attach a behavior-identical 100/100 SLA whose
/// threshold mirrors the check's legacy ms value.
/// </summary>
internal static class SlaTestUtil
{
    public static SlaNormalizationService Normalization(SuperStatusDb db)
        => new(db,
            new StatusCheckRepository(db),
            new Repository<Sla>(db),
            new Repository<BackfillReport>(db),
            NullLogger<SlaNormalizationService>.Instance);

    /// <summary>Assign every check lacking an SLA one (the same code path the
    /// startup backfill runs).</summary>
    public static void RunBackfill(SuperStatusDb db)
        => Normalization(db).BackfillAsync(dryRun: false).GetAwaiter().GetResult();

    /// <summary>A behavior-identical (Target/Critical 100) SLA mirroring the
    /// check's legacy ms value — for fixtures that build checks in memory.</summary>
    public static Sla Mirror(long slowThresholdMs) => new()
    {
        Name = $"SLA {slowThresholdMs} ms",
        TargetUptimePercent = 100,
        CriticalUptimePercent = 100,
        SlowThresholdMs = slowThresholdMs,
        IsDefault = false,
        CreatedUtc = DateTime.UtcNow,
    };

    /// <summary>Attach a behavior-identical SLA to a check that doesn't have
    /// one. (#291 Phase D dropped the legacy ms column, so fixtures state the
    /// threshold explicitly; 1000 was the common fixture value.)</summary>
    public static void Attach(StatusCheck check, long slowThresholdMs = 1000) => check.Sla ??= Mirror(slowThresholdMs);
}
