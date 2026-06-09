using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories;

/// <summary>
/// Issue #241 Phase C: persistence for browser Web Push subscriptions. Lookups are
/// by <see cref="PushSubscription.Endpoint"/> (the unique key); the alert engine
/// reads them all to fan a notification out, and prunes a dead one by endpoint.
/// </summary>
public interface IPushSubscriptionRepository : IRepository<PushSubscription>
{
    /// <summary>The subscription for a push endpoint, or null. Used to upsert on re-subscribe.</summary>
    Task<PushSubscription?> GetByEndpointAsync(string endpoint, CancellationToken cancellationToken = default);

    /// <summary>All subscriptions (untracked) — the alert engine's fan-out list.</summary>
    Task<List<PushSubscription>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete the subscription with this endpoint. Single SQL DELETE; returns the
    /// row count (0 if it was already gone). Used both for operator unsubscribe and for
    /// pruning an endpoint the push service reports as 404/410.</summary>
    Task<int> DeleteByEndpointAsync(string endpoint, CancellationToken cancellationToken = default);

    /// <summary>How many devices are subscribed (for the console summary).</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public class PushSubscriptionRepository : Repository<PushSubscription>, IPushSubscriptionRepository
{
    public PushSubscriptionRepository(SuperStatusDb context) : base(context) { }

    public async Task<PushSubscription?> GetByEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
        => await DbSet.FirstOrDefaultAsync(x => x.Endpoint == endpoint, cancellationToken);

    public async Task<List<PushSubscription>> GetAllAsync(CancellationToken cancellationToken = default)
        => await DbSet.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<int> DeleteByEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
        => await DbSet.Where(x => x.Endpoint == endpoint).ExecuteDeleteAsync(cancellationToken);

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
        => await DbSet.CountAsync(cancellationToken);
}
