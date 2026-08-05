using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Services.Services;

/// <summary>
/// Issue #293 Phase A: one check's outcome of the SLA assignment, collected
/// into the persisted backfill report.
/// </summary>
public sealed class SlaCheckSummary
{
    public long StatusCheckId { get; set; }
    public string CheckTitle { get; set; } = string.Empty;
    public string SlaName { get; set; } = string.Empty;
    public long SlowThresholdMs { get; set; }
    public bool SlaCreated { get; set; }
}

/// <summary>Issue #293 Phase A: summary of an SLA backfill run (real or preview).</summary>
public sealed class SlaBackfillSummary
{
    public DateTime GeneratedUtc { get; set; }
    public bool DryRun { get; set; }
    public int ChecksExamined { get; set; }
    public int SlasCreated { get; set; }
    public int AssignmentsMade { get; set; }
    public bool SeededDefault { get; set; }
    public List<SlaCheckSummary> Checks { get; set; } = new();
}

/// <summary>
/// Issue #293: the SLA-link writer behind the edit endpoint and the startup
/// backfill. Phase C (#291 Phase D) removed the legacy-ms translation path —
/// the ExpectedResponseTimeInMs column is gone; any historical legacy config
/// was translated by the DropLegacyEmbeddedNotificationColumns migration's
/// raw SQL. What remains here: explicit SLA links on edit, the default-SLA
/// seed, and assigning the default to SLA-less checks.
/// </summary>
public interface ISlaNormalizationService
{
    /// <summary>
    /// Make the check's SLA link reflect an edit payload. A non-null
    /// <paramref name="requestedSlaId"/> wins (unknown ids are 422'd at the
    /// API before this runs). Null → a NEW check links to the IsDefault SLA;
    /// an existing check keeps its current link (no legacy-ms fallback since
    /// Phase C). Saves; callers wrap it in the edit transaction.
    /// </summary>
    Task ApplyEditSlaAsync(StatusCheck check, long? requestedSlaId, bool isNewCheck, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seed the default SLA when none exists and assign it to every check
    /// lacking one. Idempotent: a second run finds nothing to do.
    /// <paramref name="dryRun"/> computes the summary without writing (the
    /// admin preview endpoint); a real run that changed anything persists a
    /// <see cref="BackfillReport"/> row.
    /// </summary>
    Task<SlaBackfillSummary> BackfillAsync(bool dryRun, CancellationToken cancellationToken = default);

    /// <summary>
    /// Make exactly this SLA the default — clear-old + set-new inside one
    /// transaction so the partial unique index never sees two defaults and a
    /// failure can't leave zero. False when the id is unknown (→ 404).
    /// </summary>
    Task<bool> SetDefaultAsync(long slaId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class SlaNormalizationService(
    SuperStatusDb db,
    IStatusCheckRepository statusCheckRepository,
    IRepository<Sla> slaRepository,
    IRepository<BackfillReport> backfillReportRepository,
    ILogger<SlaNormalizationService> logger) : ISlaNormalizationService
{
    public const string BackfillKind = "slas";

    /// <summary>The seeded default's threshold.</summary>
    public const long SeedDefaultSlowThresholdMs = 1000;

    // ---- edit path ----------------------------------------------------------

    public async Task ApplyEditSlaAsync(StatusCheck check, long? requestedSlaId, bool isNewCheck, CancellationToken cancellationToken = default)
    {
        if (requestedSlaId is long id)
        {
            var sla = await slaRepository.FirstOrDefault(s => s.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown SLA id {id} (the API validates ids before saving).");
            check.Sla = sla;
            check.SlaId = sla.Id;
        }
        else if (isNewCheck)
        {
            // New check without an explicit SLA → the operator's default.
            var def = await slaRepository.FirstOrDefault(s => s.IsDefault, cancellationToken)
                ?? throw new InvalidOperationException("No default SLA exists — the startup backfill seeds one before the API serves traffic (#293).");
            check.Sla = def;
            check.SlaId = def.Id;
        }
        else
        {
            // Existing check, SlaId omitted → keep the current link untouched
            // (the legacy-ms translation fallback is gone since Phase C).
            return;
        }

        await slaRepository.SaveChangesAsync(cancellationToken);
    }

    // ---- backfill ------------------------------------------------------------

    public async Task<SlaBackfillSummary> BackfillAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var summary = new SlaBackfillSummary { GeneratedUtc = DateTime.UtcNow, DryRun = dryRun };

        var def = (await slaRepository.GetMany(cancellationToken)).OrderBy(s => s.Id).FirstOrDefault(s => s.IsDefault);
        if (def is null)
        {
            // Fresh install (or a deleted-everything edge): seed the canonical
            // Default so new checks always have a target.
            def = new Sla
            {
                Name = "Default",
                TargetUptimePercent = 100,
                CriticalUptimePercent = 100,
                SlowThresholdMs = SeedDefaultSlowThresholdMs,
                IsDefault = true,
                CreatedUtc = DateTime.UtcNow,
            };
            if (!dryRun) await slaRepository.Add(def);
            summary.SlasCreated++;
            summary.SeededDefault = true;
        }

        // Any check still lacking an SLA links to the default. Legacy per-check
        // ms values were translated to their own SLAs by the
        // DropLegacyEmbeddedNotificationColumns migration; this only catches
        // rows created outside the API (manual inserts, pre-Sla seeds).
        var checks = (await statusCheckRepository.GetMany(cancellationToken)).OrderBy(c => c.Id).ToList();
        summary.ChecksExamined = checks.Count;
        foreach (var check in checks.Where(c => c.SlaId is null && c.Sla is null))
        {
            if (!dryRun) check.Sla = def;
            summary.AssignmentsMade++;
            summary.Checks.Add(new SlaCheckSummary
            {
                StatusCheckId = check.Id,
                CheckTitle = check.Title,
                SlaName = def.Name,
                SlowThresholdMs = def.SlowThresholdMs,
                SlaCreated = false,
            });
        }

        if (!dryRun && (summary.SlasCreated > 0 || summary.AssignmentsMade > 0))
        {
            await slaRepository.SaveChangesAsync(cancellationToken);
            await backfillReportRepository.AddAndSave(new BackfillReport
            {
                Kind = BackfillKind,
                CreatedUtc = summary.GeneratedUtc,
                SummaryJson = JsonSerializer.Serialize(summary),
            }, cancellationToken);
            logger.LogInformation(
                "SLA backfill: {Created} SLA(s) created, {Assigned} check(s) assigned (default seeded: {Seeded})",
                summary.SlasCreated, summary.AssignmentsMade, summary.SeededDefault);
        }

        return summary;
    }

    // ---- default switch --------------------------------------------------------

    public async Task<bool> SetDefaultAsync(long slaId, CancellationToken cancellationToken = default)
    {
        var target = await db.SlaSet.FirstOrDefaultAsync(s => s.Id == slaId, cancellationToken);
        if (target is null) return false;
        if (target.IsDefault) return true;   // already the default — nothing to switch

        // Clear-then-set in two SaveChanges inside ONE transaction: the partial
        // unique index is not deferrable, so setting the new flag while the old
        // row still carries it would fail mid-flight; the transaction makes the
        // switch atomic (a crash can't commit a zero-default state).
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var currentDefaults = await db.SlaSet.Where(s => s.IsDefault && s.Id != slaId).ToListAsync(cancellationToken);
        foreach (var sla in currentDefaults) sla.IsDefault = false;
        await db.SaveChangesAsync(cancellationToken);
        target.IsDefault = true;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }
}
