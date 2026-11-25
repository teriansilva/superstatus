namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. A pluggable delivery channel — the delivery sibling of
/// <c>SuperStatus.Services.Providers.ICheckProvider</c>. A channel answers "how do I
/// deliver this alert" (<see cref="SendAsync"/>) and describes itself
/// (<see cref="Descriptor"/>). It owns no scheduling / dedup / state — the engine
/// (<c>AlertEvaluator</c>) decides when to fire and records the audit row.
/// <para>
/// <b>Trust boundary (shared with #271):</b> in-process channel providers are trusted,
/// first-party-reviewed C#. Untrusted / community channels are out of scope until the
/// shared out-of-process protocol (#271 Phase 4) and must never be loaded in-process.
/// </para>
/// </summary>
public interface INotificationProvider
{
    /// <summary>Static description: id, display name, icon, one-sentence blurb, and
    /// whether it supports a test send.</summary>
    NotificationDescriptor Descriptor { get; }

    /// <summary>
    /// Deliver one alert. Implementations should be defensive and prefer returning a
    /// <see cref="NotificationSendResult"/> (Skipped/Failed) over throwing, but the
    /// engine also wraps every call in a try/catch, so a throw is contained as a Failed
    /// audit row and never reaches the scheduler tick.
    /// </summary>
    Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default);
}
