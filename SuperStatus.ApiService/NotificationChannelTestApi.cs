using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Notifications;
using SuperStatus.Services.Providers;

namespace SuperStatus.ApiService;

/// <summary>
/// #365: operator-only per-channel test send — the delivery sibling of the webhook
/// test-fire (<c>/admin/webhooks/{id}/test</c>) and the email test (<c>/admin/email/test</c>),
/// but generic over any registered notification channel that declares
/// <see cref="NotificationDescriptor.SupportsTest"/>. It sends one synthetic alert through the
/// provider using the <b>effective</b> config (the operator's typed values merged with the
/// stored secret via the <c>ProviderConfigWriter</c> "leave blank to keep" rule) and returns
/// the unified <see cref="NotificationSendResult"/> inline. Nothing is persisted; a secret is
/// never echoed back (parity with the audit log's safe <c>Target</c>).
/// </summary>
public static class NotificationChannelTestApi
{
    public static void MapNotificationChannelTestApi(this IEndpointRouteBuilder app)
    {
        // Operator-only: a test send performs a real outbound delivery with the channel's
        // credential — same trust level as saving it.
        app.MapPost("/notifications/providers/{type}/test",
            async (string type, ChannelTestRequest? body, INotificationProviderRegistry registry,
                   IRepository<AlertProfileChannel> channels, CancellationToken ct) =>
            {
                var outcome = await RunAsync(type, body ?? new ChannelTestRequest(), registry, channels, ct);
                return outcome.Status switch
                {
                    ChannelTestStatus.UnknownProvider => Results.NotFound(new { message = $"No notification channel '{type}' is registered." }),
                    ChannelTestStatus.NotTestable => Results.UnprocessableEntity(new { message = outcome.Message }),
                    ChannelTestStatus.InvalidConfig => Results.UnprocessableEntity(new { message = outcome.Message }),
                    _ => Results.Ok(outcome.Body),
                };
            }).RequireAuthorization();
    }

    public enum ChannelTestStatus { Ok, UnknownProvider, NotTestable, InvalidConfig }

    public sealed record ChannelTestOutcome(ChannelTestStatus Status, string? Message, ChannelTestResultViewModel? Body);

    /// <summary>
    /// The HTTP-free core, so the resolve → effective-config → validate → send flow is unit
    /// testable against an in-memory registry + repo (mirrors the channel-config tests). An
    /// unknown type ⇒ <see cref="ChannelTestStatus.UnknownProvider"/>; a non-testable channel ⇒
    /// <see cref="ChannelTestStatus.NotTestable"/>; a config that fails its schema (e.g. a
    /// required secret with nothing typed and nothing stored) ⇒
    /// <see cref="ChannelTestStatus.InvalidConfig"/> carrying the same message the save path
    /// returns. Otherwise the channel is actually fired and the result mapped inline.
    /// </summary>
    public static async Task<ChannelTestOutcome> RunAsync(
        string type, ChannelTestRequest body, INotificationProviderRegistry registry,
        IRepository<AlertProfileChannel> channels, CancellationToken ct)
    {
        var provider = registry.Find(type);
        if (provider is null)
            return new(ChannelTestStatus.UnknownProvider, null, null);

        var descriptor = provider.Descriptor;
        if (!descriptor.SupportsTest)
            return new(ChannelTestStatus.NotTestable, $"The '{descriptor.DisplayName}' channel does not support a test send.", null);

        var schema = descriptor.ConfigSchema;

        // A blank secret preserves the stored credential — so an operator can test an existing
        // channel without re-typing it; a freshly-typed URL tests a not-yet-saved channel
        // (ProfileId == 0 ⇒ nothing stored, the typed values stand alone).
        string? storedJson = body.ProfileId > 0
            ? (await channels.FirstOrDefault(c => c.AlertProfileId == body.ProfileId && c.ProviderType == descriptor.TypeId, ct))?.ConfigJson
            : null;
        var effectiveJson = ProviderConfigWriter.Build(schema, body.Config ?? new Dictionary<string, string>(), storedJson);

        var reason = schema.Validate(effectiveJson);
        if (reason is not null)
            return new(ChannelTestStatus.InvalidConfig, $"{descriptor.DisplayName} channel: {reason}.", null);

        var context = new NotificationContext(TestCheck(), AlertTrigger.Failure, recipientsOverride: null, configJson: effectiveJson);

        NotificationSendResult send;
        try
        {
            send = await provider.SendAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A misbehaving channel must not surface an exception to the operator — mirror the
            // engine's containment and report a calm Failed (never leak the target/creds).
            send = NotificationSendResult.Failed(string.Empty, "channel error");
        }

        return new(ChannelTestStatus.Ok, null, new ChannelTestResultViewModel
        {
            Outcome = send.Outcome.ToString().ToLowerInvariant(),
            Ok = send.Ok,
            // Sent ⇒ the safe label; Skipped/Failed ⇒ the reason. Never a secret.
            Detail = string.IsNullOrWhiteSpace(send.Detail) ? (send.Ok ? send.Target : null) : send.Detail,
        });
    }

    /// <summary>A throwaway check the synthetic test alert is "about" — never persisted.</summary>
    private static StatusCheck TestCheck() => new()
    {
        Title = "Test alert — SuperStatus",
        StatusCheckUrl = "https://status.superstatus.io/",
        ServiceLogoUrl = string.Empty,
        ConsecutiveFailures = 1,
    };
}
