using SuperStatus.Services.Alerts;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. The browser Web Push (VAPID) delivery channel — a thin adapter over
/// the existing <see cref="IWebPushNotifier"/>. All push behavior (VAPID signing, per-
/// endpoint timeout, expired-subscription pruning, sanitized errors) lives in the
/// notifier and is unchanged. <see cref="NotificationDescriptor.SupportsTest"/> is false:
/// web push has no test-send path today (a safe test target is a later, explicit step).
/// </summary>
public sealed class WebPushNotificationProvider(IWebPushNotifier webPushNotifier) : INotificationProvider
{
    public const string TypeId = "webpush";

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Browser push",
        icon: "bell",
        description: "Pushes alerts to subscribed browsers via Web Push (VAPID).",
        supportsTest: false);

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var result = await webPushNotifier.SendAlertAsync(context.Check, context.Trigger, cancellationToken);
        return result.Status switch
        {
            WebPushSendStatus.Sent => NotificationSendResult.Sent(result.Target),
            WebPushSendStatus.Skipped => NotificationSendResult.Skipped(result.Detail ?? string.Empty),
            _ => NotificationSendResult.Failed(result.Target, result.Detail),
        };
    }
}
