namespace SuperStatus.Services.Providers
{
    /// <summary>
    /// Epic #271 / #312 Phase 1. The set of registered (trusted, in-process) check
    /// providers, keyed by <see cref="ProviderDescriptor.TypeId"/>. The edit dialog
    /// enumerates <see cref="Descriptors"/> to populate the Type selector; the engine
    /// looks a provider up by a check's <c>ProviderType</c> at probe time.
    /// </summary>
    public interface ICheckProviderRegistry
    {
        /// <summary>The default provider id (<c>http</c>) used when a check carries no
        /// explicit <c>ProviderType</c> — preserves pre-#312 behavior.</summary>
        string DefaultTypeId { get; }

        /// <summary>Find a provider by id, or null if none is registered for it.</summary>
        ICheckProvider? Find(string? typeId);

        /// <summary>All registered providers' descriptors, ordered by display name.</summary>
        IReadOnlyList<ProviderDescriptor> Descriptors { get; }
    }
}
