using SuperStatus.Services.Providers.Http;

namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. Default <see cref="ICheckProviderRegistry"/>: built from
    /// the DI-registered <see cref="ICheckProvider"/> set. A duplicate <c>TypeId</c> is a
    /// wiring bug and fails fast at construction.
    /// </summary>
    public sealed class CheckProviderRegistry : ICheckProviderRegistry
    {
        private readonly Dictionary<string, ICheckProvider> _byType;

        public CheckProviderRegistry(IEnumerable<ICheckProvider> providers)
        {
            _byType = new Dictionary<string, ICheckProvider>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in providers)
            {
                if (!_byType.TryAdd(p.Descriptor.TypeId, p))
                    throw new InvalidOperationException($"Duplicate check provider TypeId '{p.Descriptor.TypeId}'.");
            }
            Descriptors = _byType.Values
                .Select(p => p.Descriptor)
                .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string DefaultTypeId => HttpCheckProvider.TypeId;

        public ICheckProvider? Find(string? typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId)) typeId = DefaultTypeId;
            return _byType.TryGetValue(typeId, out var provider) ? provider : null;
        }

        public IReadOnlyList<ProviderDescriptor> Descriptors { get; }
    }
}
