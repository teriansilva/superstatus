using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Services;

/// <summary>
/// Issue #291: the link writer behind the check edit endpoint. Phase D removed
/// the legacy embedded webhook/alert fields (and with them the translation +
/// startup-backfill paths that read them) — any historical legacy config was
/// translated into linked entities by the DropLegacyEmbeddedNotificationColumns
/// migration's raw SQL. What remains here: explicit id-array link replacement
/// on edit, id validation, and the naming/normalization helpers the admin API
/// shares.
/// </summary>
public interface ILinkedTargetNormalizationService
{
    /// <summary>
    /// Make the check's links reflect an edit payload. A non-null list
    /// replaces the family's links with exactly those ids (removed links drop
    /// their throttle anchors; kept links keep theirs). A null list means
    /// "not provided" → that family's links are left unchanged (since Phase D
    /// there is no legacy-field translation fallback).
    /// </summary>
    Task ApplyEditLinksAsync(StatusCheck check, IReadOnlyCollection<long>? webhookIds, IReadOnlyCollection<long>? alertProfileIds, CancellationToken cancellationToken = default);

    /// <summary>Ids in the lists that don't exist (the API turns these into a 422 before saving anything).</summary>
    Task<List<long>> FindMissingWebhookIdsAsync(IReadOnlyCollection<long> webhookIds, CancellationToken cancellationToken = default);
    Task<List<long>> FindMissingAlertProfileIdsAsync(IReadOnlyCollection<long> alertProfileIds, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class LinkedTargetNormalizationService(
    IStatusCheckLinkRepository linkRepository,
    IRepository<Webhook> webhookRepository,
    IRepository<AlertProfile> alertProfileRepository) : ILinkedTargetNormalizationService
{
    // ---------- pure helpers (also used by the API + tests) ----------

    /// <summary>Canonical recipient set: comma-or-space split, trim, lower, sort, distinct.</summary>
    public static List<string> NormalizeRecipients(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.ToLowerInvariant())
            .Distinct()
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Webhook auto-name: the URL host, "#N"-suffixed on collision.</summary>
    public static string AutoWebhookName(string url, ICollection<string> existingNames)
    {
        string baseName = Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? u.Host
            : url.Trim();
        return Deduplicate(baseName, existingNames);
    }

    /// <summary>Profile auto-name: "Default recipients" for the site-default
    /// profile, first recipient (+N) for explicit lists, "Web push" for
    /// push-only; "#N"-suffixed on collision.</summary>
    public static string AutoProfileName(bool usesSiteDefaultRecipients, IReadOnlyList<string> recipients, ICollection<string> existingNames)
    {
        string baseName = usesSiteDefaultRecipients
            ? "Default recipients"
            : recipients.Count > 0
                ? recipients.Count == 1 ? recipients[0] : $"{recipients[0]} +{recipients.Count - 1}"
                : "Web push";
        return Deduplicate(baseName, existingNames);
    }

    /// <summary>"#N"-suffix a name until it's unique in <paramref name="existingNames"/>.
    /// Public since #293 — the SLA surfaces use the same collision rule.</summary>
    public static string Deduplicate(string baseName, ICollection<string> existingNames)
    {
        if (!existingNames.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} #{n}";
            if (!existingNames.Contains(candidate)) return candidate;
        }
    }

    // ---------- edit path ----------

    public async Task ApplyEditLinksAsync(StatusCheck check, IReadOnlyCollection<long>? webhookIds, IReadOnlyCollection<long>? alertProfileIds, CancellationToken cancellationToken = default)
    {
        if (webhookIds is not null)
        {
            var webhookLinks = await linkRepository.GetWebhookLinksAsync(check.Id, cancellationToken);
            ReplaceLinks(webhookLinks, webhookIds,
                l => l.WebhookId,
                id => linkRepository.AddWebhookLink(new StatusCheckWebhook { StatusCheckId = check.Id, WebhookId = id }),
                l => linkRepository.RemoveWebhookLink(l));
        }

        if (alertProfileIds is not null)
        {
            var profileLinks = await linkRepository.GetAlertProfileLinksAsync(check.Id, cancellationToken);
            ReplaceLinks(profileLinks, alertProfileIds,
                l => l.AlertProfileId,
                id => linkRepository.AddAlertProfileLink(new StatusCheckAlertProfile { StatusCheckId = check.Id, AlertProfileId = id }),
                l => linkRepository.RemoveAlertProfileLink(l));
        }

        await linkRepository.SaveChangesAsync(cancellationToken);
    }

    private static void ReplaceLinks<TLink>(List<TLink> current, IReadOnlyCollection<long> desiredIds,
        Func<TLink, long> idOf, Action<long> add, Action<TLink> remove)
    {
        var desired = desiredIds.Distinct().ToHashSet();
        foreach (var link in current.Where(l => !desired.Contains(idOf(l))))
            remove(link);
        var kept = current.Select(idOf).ToHashSet();
        foreach (var id in desired.Where(id => !kept.Contains(id)))
            add(id);   // explicit links start with fresh (null) throttle anchors
    }

    // ---------- id validation ----------

    public async Task<List<long>> FindMissingWebhookIdsAsync(IReadOnlyCollection<long> webhookIds, CancellationToken cancellationToken = default)
    {
        var distinct = webhookIds.Distinct().ToList();
        var known = (await webhookRepository.GetMany(cancellationToken)).Select(w => w.Id).ToHashSet();
        return distinct.Where(id => !known.Contains(id)).ToList();
    }

    public async Task<List<long>> FindMissingAlertProfileIdsAsync(IReadOnlyCollection<long> alertProfileIds, CancellationToken cancellationToken = default)
    {
        var distinct = alertProfileIds.Distinct().ToList();
        var known = (await alertProfileRepository.GetMany(cancellationToken)).Select(p => p.Id).ToHashSet();
        return distinct.Where(id => !known.Contains(id)).ToList();
    }
}
