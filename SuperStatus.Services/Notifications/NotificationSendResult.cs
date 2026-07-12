namespace SuperStatus.Services.Notifications;

/// <summary>What happened to a channel send at the notification seam. Mirrors the
/// per-notifier statuses (<c>EmailSendStatus</c> / <c>WebPushSendStatus</c>) so the
/// evaluator maps one shape to the audit log regardless of channel.</summary>
public enum NotificationOutcome
{
    /// <summary>Delivered.</summary>
    Sent,
    /// <summary>Intentionally not attempted (not configured / nobody to send to) — NOT a failure.</summary>
    Skipped,
    /// <summary>Attempted but errored.</summary>
    Failed,
}

/// <summary>
/// Phase 1 of #343. The unified result a channel (<see cref="INotificationProvider"/>)
/// returns, replacing the parallel <c>EmailSendResult</c> / <c>WebPushSendResult</c> at
/// the seam boundary. <see cref="NotificationOutcome.Skipped"/> (a guard, no attempt) is
/// distinct from <see cref="NotificationOutcome.Failed"/> (an attempted delivery that
/// errored) so the audit log never shows an unconfigured channel as a delivery outage.
/// </summary>
public sealed record NotificationSendResult(NotificationOutcome Outcome, string Target, string? Detail)
{
    public bool Ok => Outcome == NotificationOutcome.Sent;

    public static NotificationSendResult Sent(string target) => new(NotificationOutcome.Sent, target, null);
    public static NotificationSendResult Skipped(string reason) => new(NotificationOutcome.Skipped, string.Empty, reason);
    public static NotificationSendResult Failed(string target, string? error) => new(NotificationOutcome.Failed, target, error);
}
