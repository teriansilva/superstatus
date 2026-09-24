namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. The set of registered (trusted, in-process) notification channels,
/// keyed by <see cref="NotificationDescriptor.TypeId"/> — the delivery sibling of
/// <c>ICheckProviderRegistry</c>. The engine looks a channel up by its stable TypeId at
/// dispatch time; the (Phase 2) Plugins page enumerates <see cref="Descriptors"/>.
/// </summary>
public interface INotificationProviderRegistry
{
    /// <summary>Find a channel by id, or null if none is registered for it.</summary>
    INotificationProvider? Find(string? typeId);

    /// <summary>All registered channels' descriptors, ordered by display name.</summary>
    IReadOnlyList<NotificationDescriptor> Descriptors { get; }
}
