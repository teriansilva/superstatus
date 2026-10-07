using SuperStatus.Services.Alerts;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. The email (SMTP) delivery channel — a thin adapter over the existing
/// <see cref="IEmailNotifier"/>. All SMTP behavior (relay config, per-send timeout,
/// recipient fallback, sanitized errors) lives in the notifier and is unchanged; this
/// only maps the call onto the notification seam.
/// </summary>
public sealed class EmailNotificationProvider(IEmailNotifier emailNotifier) : INotificationProvider
{
    public const string TypeId = "email";

    private static readonly NotificationDescriptor _descriptor = new(
        typeId: TypeId,
        displayName: "Email (SMTP)",
        icon: "mail",
        description: "Sends alert emails through the operator-configured SMTP relay.",
        supportsTest: true);

    public NotificationDescriptor Descriptor => _descriptor;

    public async Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var result = await emailNotifier.SendAlertAsync(context.Check, context.Trigger, context.RecipientsOverride, cancellationToken);
        return result.Status switch
        {
            EmailSendStatus.Sent => NotificationSendResult.Sent(result.Target),
            EmailSendStatus.Skipped => NotificationSendResult.Skipped(result.Detail ?? string.Empty),
            _ => NotificationSendResult.Failed(result.Target, result.Detail),
        };
    }
}
