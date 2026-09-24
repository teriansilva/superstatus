using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories;

/// <summary>
/// Issue #291 Phase A: persistence for the StatusCheck↔Webhook and
/// StatusCheck↔AlertProfile link tables. Composite-PK link rows don't fit the
/// generic <see cref="Repository{T}"/> (no single Id), so the link operations
/// the dispatchers + normalization service need live here.
/// </summary>
public interface IStatusCheckLinkRepository
{
    /// <summary>A check's webhook links with the target eager-loaded, tracked —
    /// dispatch mutates the per-link throttle anchor in place.</summary>
    Task<List<StatusCheckWebhook>> GetWebhookLinksAsync(long statusCheckId, CancellationToken cancellationToken = default);

    /// <summary>A check's alert-profile links with the target eager-loaded, tracked.</summary>
    Task<List<StatusCheckAlertProfile>> GetAlertProfileLinksAsync(long statusCheckId, CancellationToken cancellationToken = default);

    /// <summary>Batched linked-id lookup for a set of checks (one query per link
    /// table) — backs the list GET's round-trip ids without an N+1.</summary>
    Task<Dictionary<long, List<long>>> GetLinkedWebhookIdsAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default);
    Task<Dictionary<long, List<long>>> GetLinkedAlertProfileIdsAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default);

    /// <summary>Per-webhook linked check titles (ordered), for LinkedEntitySummary.</summary>
    Task<Dictionary<long, List<string>>> GetWebhookLinkedCheckTitlesAsync(CancellationToken cancellationToken = default);

    /// <summary>Per-profile linked check titles (ordered), for LinkedEntitySummary.</summary>
    Task<Dictionary<long, List<string>>> GetAlertProfileLinkedCheckTitlesAsync(CancellationToken cancellationToken = default);

    void AddWebhookLink(StatusCheckWebhook link);
    void RemoveWebhookLink(StatusCheckWebhook link);
    void AddAlertProfileLink(StatusCheckAlertProfile link);
    void RemoveAlertProfileLink(StatusCheckAlertProfile link);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public class StatusCheckLinkRepository(SuperStatusDb context) : IStatusCheckLinkRepository
{
    public async Task<List<StatusCheckWebhook>> GetWebhookLinksAsync(long statusCheckId, CancellationToken cancellationToken = default)
    {
        return await context.StatusCheckWebhookSet
            .Include(x => x.Webhook)
            .Where(x => x.StatusCheckId == statusCheckId)
            .OrderBy(x => x.WebhookId)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<StatusCheckAlertProfile>> GetAlertProfileLinksAsync(long statusCheckId, CancellationToken cancellationToken = default)
    {
        return await context.StatusCheckAlertProfileSet
            .Include(x => x.AlertProfile)
                // #343 Phase 3: the profile's channel collection drives delivery.
                .ThenInclude(p => p!.Channels)
            .Where(x => x.StatusCheckId == statusCheckId)
            .OrderBy(x => x.AlertProfileId)
            .ToListAsync(cancellationToken);
    }

    public async Task<Dictionary<long, List<long>>> GetLinkedWebhookIdsAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default)
    {
        var rows = await context.StatusCheckWebhookSet
            .AsNoTracking()
            .Where(x => statusCheckIds.Contains(x.StatusCheckId))
            .OrderBy(x => x.WebhookId)
            .Select(x => new { x.StatusCheckId, x.WebhookId })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.StatusCheckId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.WebhookId).ToList());
    }

    public async Task<Dictionary<long, List<long>>> GetLinkedAlertProfileIdsAsync(IReadOnlyCollection<long> statusCheckIds, CancellationToken cancellationToken = default)
    {
        var rows = await context.StatusCheckAlertProfileSet
            .AsNoTracking()
            .Where(x => statusCheckIds.Contains(x.StatusCheckId))
            .OrderBy(x => x.AlertProfileId)
            .Select(x => new { x.StatusCheckId, x.AlertProfileId })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.StatusCheckId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.AlertProfileId).ToList());
    }

    public async Task<Dictionary<long, List<string>>> GetWebhookLinkedCheckTitlesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.StatusCheckWebhookSet
            .AsNoTracking()
            .Select(x => new { x.WebhookId, x.StatusCheck!.Title })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.WebhookId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Title).OrderBy(t => t).ToList());
    }

    public async Task<Dictionary<long, List<string>>> GetAlertProfileLinkedCheckTitlesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.StatusCheckAlertProfileSet
            .AsNoTracking()
            .Select(x => new { x.AlertProfileId, x.StatusCheck!.Title })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.AlertProfileId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Title).OrderBy(t => t).ToList());
    }

    public void AddWebhookLink(StatusCheckWebhook link) => context.StatusCheckWebhookSet.Add(link);
    public void RemoveWebhookLink(StatusCheckWebhook link) => context.StatusCheckWebhookSet.Remove(link);
    public void AddAlertProfileLink(StatusCheckAlertProfile link) => context.StatusCheckAlertProfileSet.Add(link);
    public void RemoveAlertProfileLink(StatusCheckAlertProfile link) => context.StatusCheckAlertProfileSet.Remove(link);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => context.SaveChangesAsync(cancellationToken);
}
