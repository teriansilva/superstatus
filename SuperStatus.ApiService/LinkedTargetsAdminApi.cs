using System.Text.Json;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Notifications;
using SuperStatus.Services.Plugins;
using SuperStatus.Services.Providers;
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

        app.MapGet("/admin/alert-profiles", async (IRepository<AlertProfile> profiles, IRepository<AlertProfileChannel> channels, IStatusCheckLinkRepository links, INotificationProviderRegistry registry, CancellationToken ct) =>
        {
            var all = (await profiles.GetMany(ct)).OrderBy(p => p.Id).ToList();
            var titles = await links.GetAlertProfileLinkedCheckTitlesAsync(ct);
            // #343 Phase 5: attach each profile's schema-driven channels (webhook/slack/…),
            // secrets masked. One channels read, grouped in memory (low-traffic admin surface).
            var channelsByProfile = (await channels.GetMany(ct))
                .GroupBy(c => c.AlertProfileId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var result = all.Select(p => new AlertProfileViewModel(p, titles.GetValueOrDefault(p.Id))
            {
                Channels = ProjectSchemaChannels(channelsByProfile.GetValueOrDefault(p.Id), registry),
            }).ToList();
            return Results.Ok(result);
        }).RequireAuthorization();

        app.MapPost("/admin/alert-profiles", async (AlertProfileViewModel body, IRepository<AlertProfile> profiles, IRepository<AlertProfileChannel> channels, IStatusCheckLinkRepository links, INotificationProviderRegistry registry, CancellationToken ct) =>
        {
            string? invalid = ValidateAlertProfile(body);
            if (invalid is not null)
                return Results.UnprocessableEntity(new { message = invalid });

            // #343 Phase 5: an ENABLED schema-driven channel (webhook/slack/…) must carry a
            // usable config, else it would save as "enabled" yet silently never deliver (the
            // provider returns Skipped at send time). Validate the EFFECTIVE config — after the
            // blank-secret-preserve rule — so re-saving with a masked (blank) secret still
            // passes when a credential is already stored.
            string? channelInvalid = await ValidateSchemaChannelsAsync(channels, body.Id, body, registry, ct);
            if (channelInvalid is not null)
                return Results.UnprocessableEntity(new { message = channelInvalid });

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
                await SyncChannelsAsync(channels, existing.Id, body, recipients, ct);
                await PersistSchemaChannelsAsync(channels, existing.Id, body, registry, ct);
                var linked = (await links.GetAlertProfileLinkedCheckTitlesAsync(ct)).GetValueOrDefault(existing.Id);
                return Results.Ok(await BuildProfileEchoAsync(existing, linked, channels, registry, ct));
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
            await SyncChannelsAsync(channels, entity.Id, body, recipients, ct);
            await PersistSchemaChannelsAsync(channels, entity.Id, body, registry, ct);
            return Results.Ok(await BuildProfileEchoAsync(entity, null, channels, registry, ct));
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

    /// <summary>#343 Phase 3: mirror the profile's email/web-push settings into its
    /// <see cref="AlertProfileChannel"/> collection (the delivery source of truth). The
    /// deprecated columns are still written above (expand/contract migration); this keeps
    /// the channel rows in sync so the engine reads the same result. Email carries its
    /// recipients + site-default flag as config; web push needs none.</summary>
    public static async Task SyncChannelsAsync(
        IRepository<AlertProfileChannel> channels, long profileId, AlertProfileViewModel body, List<string> recipients, CancellationToken ct)
    {
        var emailConfig = new Data.Entities.EmailChannelSettings(string.Join(",", recipients), body.UsesSiteDefaultRecipients).ToJson();
        await UpsertChannelAsync(channels, profileId, NotificationChannelTypes.Email, body.EmailEnabled, emailConfig, ct);
        await UpsertChannelAsync(channels, profileId, NotificationChannelTypes.WebPush, body.WebPushEnabled, null, ct);
    }

    private static async Task UpsertChannelAsync(
        IRepository<AlertProfileChannel> channels, long profileId, string providerType, bool enabled, string? configJson, CancellationToken ct)
    {
        var existing = await channels.FirstOrDefault(c => c.AlertProfileId == profileId && c.ProviderType == providerType, ct);
        if (existing is null)
        {
            await channels.AddAndSave(new AlertProfileChannel
            {
                AlertProfileId = profileId,
                ProviderType = providerType,
                IsEnabled = enabled,
                ConfigJson = configJson,
            }, ct);
        }
        else
        {
            existing.IsEnabled = enabled;
            existing.ConfigJson = configJson;
            await channels.UpdateAndSave(existing, ct);
        }
    }

    /// <summary>#343 Phase 5: validate each <b>enabled</b> schema-driven channel's
    /// <b>effective</b> config (after the blank-secret-preserve rule) against its
    /// <see cref="ConfigSchema"/>. Returns <c>null</c> when all pass, else a 422 message naming
    /// the channel + the offending field. A <b>disabled</b> channel is never validated (an
    /// operator may leave it unconfigured); a schemaless / unknown type is skipped. This is the
    /// server-authoritative guard that stops an enabled-but-uncredentialed channel from saving
    /// and then silently never delivering.</summary>
    public static async Task<string?> ValidateSchemaChannelsAsync(
        IRepository<AlertProfileChannel> channels, long profileId, AlertProfileViewModel body, INotificationProviderRegistry registry, CancellationToken ct)
    {
        foreach (var ch in body.Channels)
        {
            if (!ch.IsEnabled) continue;
            var descriptor = registry.Find(ch.ProviderType)?.Descriptor;
            var schema = descriptor?.ConfigSchema;
            if (schema is null || schema.Fields.Count == 0) continue;

            // Existing row (if any) lets a masked/blank secret re-save preserve its stored value;
            // a brand-new enable (no stored secret) with a blank required field fails here.
            var existing = profileId > 0
                ? await channels.FirstOrDefault(c => c.AlertProfileId == profileId && c.ProviderType == ch.ProviderType, ct)
                : null;
            var effectiveJson = ProviderConfigWriter.Build(schema, ch.Config ?? new Dictionary<string, string>(), existing?.ConfigJson);
            var reason = schema.Validate(effectiveJson);
            if (reason is not null)
                return $"{descriptor!.DisplayName} channel: {reason}.";
        }
        return null;
    }

    /// <summary>#343 Phase 5: persist the editor's schema-driven channels (webhook / slack /
    /// discord / telegram) through <see cref="ProviderConfigWriter"/> so a blank <c>secret</c>
    /// field preserves the stored credential ("leave blank to keep"). Channels without a
    /// config schema (email / web push) are handled by <see cref="SyncChannelsAsync"/>; unknown
    /// types are skipped.</summary>
    public static async Task PersistSchemaChannelsAsync(
        IRepository<AlertProfileChannel> channels, long profileId, AlertProfileViewModel body, INotificationProviderRegistry registry, CancellationToken ct)
    {
        foreach (var ch in body.Channels)
        {
            var schema = registry.Find(ch.ProviderType)?.Descriptor.ConfigSchema;
            if (schema is null || schema.Fields.Count == 0) continue;

            // Read the stored row first so the "blank secret preserves stored" rule can see
            // the previous ConfigJson.
            var existing = await channels.FirstOrDefault(c => c.AlertProfileId == profileId && c.ProviderType == ch.ProviderType, ct);
            var configJson = ProviderConfigWriter.Build(schema, ch.Config ?? new Dictionary<string, string>(), existing?.ConfigJson);
            await UpsertChannelAsync(channels, profileId, ch.ProviderType, ch.IsEnabled, configJson, ct);
        }
    }

    /// <summary>#343 Phase 5: project a profile's schema-driven channel rows to their wire VMs
    /// for the editor. Only channels whose provider declares config fields are included
    /// (email / web push have none and keep their bespoke controls; an unregistered type is
    /// skipped). A <c>secret</c> field's stored value is never echoed — the form shows it blank
    /// with "leave blank to keep".</summary>
    public static List<AlertProfileChannelViewModel> ProjectSchemaChannels(List<AlertProfileChannel>? rows, INotificationProviderRegistry registry)
    {
        var list = new List<AlertProfileChannelViewModel>();
        if (rows is null) return list;
        foreach (var row in rows)
        {
            var schema = registry.Find(row.ProviderType)?.Descriptor.ConfigSchema;
            if (schema is null || schema.Fields.Count == 0) continue;

            var vm = new AlertProfileChannelViewModel { ProviderType = row.ProviderType, IsEnabled = row.IsEnabled };
            var stored = FlattenConfig(row.ConfigJson);
            foreach (var field in schema.Fields)
            {
                if (field.Kind == ConfigFieldKind.Secret)
                {
                    // #365: never echo the secret value — but record that one IS stored so the
                    // editor shows "leave blank to keep" (and drops Required) instead of the
                    // trap where a new, required secret field claims it can be left blank.
                    if (stored.TryGetValue(field.Key, out var sv) && !string.IsNullOrWhiteSpace(sv))
                        vm.StoredSecretKeys.Add(field.Key);
                    continue;
                }
                if (stored.TryGetValue(field.Key, out var v)) vm.Config[field.Key] = v;
            }
            list.Add(vm);
        }
        return list;
    }

    private static async Task<AlertProfileViewModel> BuildProfileEchoAsync(
        AlertProfile profile, List<string>? linkedCheckNames, IRepository<AlertProfileChannel> channels, INotificationProviderRegistry registry, CancellationToken ct)
    {
        var rows = (await channels.GetMany(ct)).Where(c => c.AlertProfileId == profile.Id).ToList();
        return new AlertProfileViewModel(profile, linkedCheckNames)
        {
            Channels = ProjectSchemaChannels(rows, registry),
        };
    }

    /// <summary>Flatten a channel's stored ConfigJson object to a string-valued dictionary
    /// (each value stringified) — tolerant of null/blank/malformed (returns empty).</summary>
    private static Dictionary<string, string> FlattenConfig(string? json)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    d[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.ToString();
                }
            }
        }
        catch (JsonException) { }
        return d;
    }
}
