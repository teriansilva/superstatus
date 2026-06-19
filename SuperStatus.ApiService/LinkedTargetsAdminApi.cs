using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;

namespace SuperStatus.ApiService;

/// <summary>
/// Issue #291 Phase A: operator-only CRUD for linked webhook targets and alert
/// profiles, plus the backfill preview. No UI consumes these yet (that's a
/// later phase) — the contract is exercised by the API + tests only.
/// </summary>
public static class LinkedTargetsAdminApi
{
    public static void MapLinkedTargetsAdminApi(this IEndpointRouteBuilder app)
    {
        // ---- webhooks ----

        app.MapGet("/admin/webhooks", async (IRepository<Webhook> webhooks, IStatusCheckLinkRepository links, CancellationToken ct) =>
        {
            var all = (await webhooks.GetMany(ct)).OrderBy(w => w.Id).ToList();
            var titles = await links.GetWebhookLinkedCheckTitlesAsync(ct);
            return Results.Ok(all.Select(w => new WebhookViewModel(w, titles.GetValueOrDefault(w.Id))).ToList());
        }).RequireAuthorization();

        app.MapPost("/admin/webhooks", async (WebhookViewModel body, IRepository<Webhook> webhooks, IStatusCheckLinkRepository links, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Url))
                return Results.UnprocessableEntity(new { message = "url is required" });

            // #291 Phase B: the edit dialog saves through this same endpoint —
            // Id > 0 updates the existing target (links untouched; they're
            // managed from each check's dialog).
            if (body.Id > 0)
            {
                var existing = await webhooks.FirstOrDefault(w => w.Id == body.Id, ct);
                if (existing is null) return Results.NotFound();
                if (!string.IsNullOrWhiteSpace(body.Name)) existing.Name = body.Name.Trim();
                existing.Url = body.Url.Trim();
                existing.IsEnabled = body.IsEnabled;
                existing.ThrottleMinutes = Math.Max(0, body.ThrottleMinutes);
                await webhooks.UpdateAndSave(existing, ct);
                var linked = (await links.GetWebhookLinkedCheckTitlesAsync(ct)).GetValueOrDefault(existing.Id);
                return Results.Ok(new WebhookViewModel(existing, linked));
            }

            // Blank name → auto-name from the URL host, #N-suffixed on collision
            // (same rule the backfill uses).
            var names = (await webhooks.GetMany(ct)).Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
            var entity = new Webhook
            {
                Name = string.IsNullOrWhiteSpace(body.Name)
                    ? LinkedTargetNormalizationService.AutoWebhookName(body.Url.Trim(), names)
                    : body.Name.Trim(),
                Url = body.Url.Trim(),
                IsEnabled = body.IsEnabled,
                ThrottleMinutes = Math.Max(0, body.ThrottleMinutes),
                CreatedUtc = DateTime.UtcNow,
            };
            await webhooks.AddAndSave(entity, ct);
            return Results.Ok(new WebhookViewModel(entity, null));
        }).RequireAuthorization();

        // #291 Phase B: operator test-fire — one wire attempt through the same
        // executor path real dispatch uses. Result comes back inline; nothing
        // is logged (a test has no triggering check and the execution log's
        // StatusCheckId is a required FK — no schema change in this phase).
        // Deliberately ungated by IsEnabled: testing a disabled target is valid.
        app.MapPost("/admin/webhooks/{id:long}/test", async (long id, IRepository<Webhook> webhooks, IStatusCheckService statusCheckService, CancellationToken ct) =>
        {
            var webhook = await webhooks.FirstOrDefault(w => w.Id == id, ct);
            if (webhook is null) return Results.NotFound();
            var result = await statusCheckService.TestFireWebhookAsync(webhook, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        app.MapPatch("/admin/webhooks/{id:long}/enabled", async (long id, EnabledRequest body, IRepository<Webhook> webhooks, CancellationToken ct) =>
        {
            var webhook = await webhooks.FirstOrDefault(w => w.Id == id, ct);
            if (webhook is null) return Results.NotFound();
            webhook.IsEnabled = body.Enabled;
            await webhooks.UpdateAndSave(webhook, ct);
            return Results.Ok(new { id = webhook.Id, enabled = webhook.IsEnabled });
        }).RequireAuthorization();

        app.MapDelete("/admin/webhooks/{id:long}", async (long id, IRepository<Webhook> webhooks, IStatusCheckLinkRepository links, CancellationToken ct) =>
        {
            var webhook = await webhooks.FirstOrDefault(w => w.Id == id, ct);
            if (webhook is null) return Results.NotFound();

            // Delete guard: a linked target can't be deleted (409 carries the
            // same LinkedEntitySummary shape the list responses embed). The DB
            // RESTRICT FK is the backstop.
            var titles = (await links.GetWebhookLinkedCheckTitlesAsync(ct)).GetValueOrDefault(id);
            if (titles is { Count: > 0 })
                return Results.Conflict(new
                {
                    message = $"Webhook '{webhook.Name}' is linked to {titles.Count} check(s); unlink it first.",
                    usage = LinkedEntitySummary.From(titles),
                });

            await webhooks.DeleteAndSave(webhook, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // ---- alert profiles ----

        app.MapGet("/admin/alert-profiles", async (IRepository<AlertProfile> profiles, IStatusCheckLinkRepository links, CancellationToken ct) =>
        {
            var all = (await profiles.GetMany(ct)).OrderBy(p => p.Id).ToList();
            var titles = await links.GetAlertProfileLinkedCheckTitlesAsync(ct);
            return Results.Ok(all.Select(p => new AlertProfileViewModel(p, titles.GetValueOrDefault(p.Id))).ToList());
        }).RequireAuthorization();

        app.MapPost("/admin/alert-profiles", async (AlertProfileViewModel body, IRepository<AlertProfile> profiles, IStatusCheckLinkRepository links, CancellationToken ct) =>
        {
            string? invalid = ValidateAlertProfile(body);
            if (invalid is not null)
                return Results.UnprocessableEntity(new { message = invalid });

            // Recipients are stored canonical (trim/lower/sort) — the same form
            // the dedupe key uses; a site-default profile stores none.
            var recipients = body.UsesSiteDefaultRecipients
                ? new List<string>()
                : LinkedTargetNormalizationService.NormalizeRecipients(body.EmailRecipients);

            // #291 Phase C: the edit dialog saves through this same endpoint —
            // Id > 0 updates the existing profile (links untouched; they're
            // managed from each check's dialog). Mirrors the webhook upsert.
            if (body.Id > 0)
            {
                var existing = await profiles.FirstOrDefault(p => p.Id == body.Id, ct);
                if (existing is null) return Results.NotFound();
                if (!string.IsNullOrWhiteSpace(body.Name)) existing.Name = body.Name.Trim();
                existing.EmailEnabled = body.EmailEnabled;
                existing.EmailRecipients = string.Join(",", recipients);
                existing.UsesSiteDefaultRecipients = body.UsesSiteDefaultRecipients;
                existing.WebPushEnabled = body.WebPushEnabled;
                await profiles.UpdateAndSave(existing, ct);
                var linked = (await links.GetAlertProfileLinkedCheckTitlesAsync(ct)).GetValueOrDefault(existing.Id);
                return Results.Ok(new AlertProfileViewModel(existing, linked));
            }

            var names = (await profiles.GetMany(ct)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var entity = new AlertProfile
            {
                Name = string.IsNullOrWhiteSpace(body.Name)
                    ? LinkedTargetNormalizationService.AutoProfileName(body.UsesSiteDefaultRecipients, recipients, names)
                    : body.Name.Trim(),
                EmailEnabled = body.EmailEnabled,
                EmailRecipients = string.Join(",", recipients),
                UsesSiteDefaultRecipients = body.UsesSiteDefaultRecipients,
                WebPushEnabled = body.WebPushEnabled,
                CreatedUtc = DateTime.UtcNow,
            };
            await profiles.AddAndSave(entity, ct);
            return Results.Ok(new AlertProfileViewModel(entity, null));
        }).RequireAuthorization();

        app.MapDelete("/admin/alert-profiles/{id:long}", async (long id, IRepository<AlertProfile> profiles, IStatusCheckLinkRepository links, CancellationToken ct) =>
        {
            var profile = await profiles.FirstOrDefault(p => p.Id == id, ct);
            if (profile is null) return Results.NotFound();

            var titles = (await links.GetAlertProfileLinkedCheckTitlesAsync(ct)).GetValueOrDefault(id);
            if (titles is { Count: > 0 })
                return Results.Conflict(new
                {
                    message = $"Alert profile '{profile.Name}' is linked to {titles.Count} check(s); unlink it first.",
                    usage = LinkedEntitySummary.From(titles),
                });

            await profiles.DeleteAndSave(profile, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // #291 Phase D: the legacy→linked backfill (and its preview endpoint)
        // is gone — the DropLegacyEmbeddedNotificationColumns migration does
        // the translation in raw SQL before dropping the columns.
    }

    /// <summary>
    /// #291 Phase D: the accepted-and-translated window for the legacy
    /// embedded notification fields is CLOSED. A payload carrying any of them
    /// with a non-empty value is rejected (422) before anything is written —
    /// the columns are gone and there is no translation path left. The
    /// read-only LinkedWebhookIds/LinkedAlertProfileIds round-trip props are
    /// fine: a fetched VM posts those back freely.
    /// </summary>
    public static string? ValidateEditPayload(StatusCheckViewModelBase payload)
    {
        if (payload.IsWebHookOnErrorEnabled
            || !string.IsNullOrWhiteSpace(payload.WebHookOnErrorUrl)
            || payload.ThrottleWebHookToExecuteOnlyEveryXMinutes != 0)
        {
            return "The embedded webhook fields (isWebHookOnErrorEnabled/webHookOnErrorUrl/throttleWebHookToExecuteOnlyEveryXMinutes) were removed in this release — link reusable webhooks via webhookIds instead. See the release notes for the #291 linked-notifications migration.";
        }

        if (payload.EmailAlertsEnabled
            || !string.IsNullOrWhiteSpace(payload.EmailRecipients)
            || payload.WebPushAlertsEnabled)
        {
            return "The embedded alert fields (emailAlertsEnabled/emailRecipients/webPushAlertsEnabled) were removed in this release — link reusable alert profiles via alertProfileIds instead. See the release notes for the #291 linked-notifications migration.";
        }

        return null;
    }

    /// <summary>#291: email on + no recipients + no site-default fallback can
    /// never deliver — rejected with 422 (the one invalid combination).</summary>
    public static string? ValidateAlertProfile(AlertProfileViewModel body)
    {
        if (body.EmailEnabled
            && !body.UsesSiteDefaultRecipients
            && LinkedTargetNormalizationService.NormalizeRecipients(body.EmailRecipients).Count == 0)
        {
            return "Email is enabled but the profile has no recipients; set emailRecipients or usesSiteDefaultRecipients.";
        }
        return null;
    }
}
