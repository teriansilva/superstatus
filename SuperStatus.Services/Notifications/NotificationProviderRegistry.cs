namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. Default <see cref="INotificationProviderRegistry"/>, built from the
/// DI-registered <see cref="INotificationProvider"/> set. A duplicate <c>TypeId</c> is a
/// wiring bug and fails fast at construction — the same contract as
/// <c>CheckProviderRegistry</c>.
/// </summary>
public sealed class NotificationProviderRegistry : INotificationProviderRegistry
{
    private readonly Dictionary<string, INotificationProvider> _byType;

    public NotificationProviderRegistry(IEnumerable<INotificationProvider> providers)
    {
        _byType = new Dictionary<string, INotificationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            if (!_byType.TryAdd(p.Descriptor.TypeId, p))
                throw new InvalidOperationException($"Duplicate notification provider TypeId '{p.Descriptor.TypeId}'.");
        }
        Descriptors = _byType.Values
            .Select(p => p.Descriptor)
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public INotificationProvider? Find(string? typeId)
        => string.IsNullOrWhiteSpace(typeId) ? null
         : _byType.TryGetValue(typeId, out var provider) ? provider : null;

    public IReadOnlyList<NotificationDescriptor> Descriptors { get; }
}
