namespace SuperStatus.Services.Plugins
{
    /// <summary>
    /// One field in a provider's <see cref="ConfigSchema"/>. The edit dialog renders
    /// a field generically from its <see cref="Kind"/> (#271/#312); the provider reads
    /// its value back out of the stored <c>ConfigJson</c> by <see cref="Key"/>.
    /// </summary>
    /// <param name="Key">JSON property key in the stored <c>ConfigJson</c> (stable; never localised).</param>
    /// <param name="Label">Human label shown in the form.</param>
    /// <param name="Kind">Which vocabulary kind to render / validate as.</param>
    /// <param name="Required">Whether a (non-empty) value must be present.</param>
    /// <param name="Help">Optional helper text shown under the field.</param>
    /// <param name="Options">For <see cref="ConfigFieldKind.Select"/>: the allowed values + labels, in order.</param>
    /// <param name="Placeholder">Optional placeholder / example.</param>
    public sealed record ConfigField(
        string Key,
        string Label,
        ConfigFieldKind Kind,
        bool Required = false,
        string? Help = null,
        IReadOnlyList<ConfigSelectOption>? Options = null,
        string? Placeholder = null);

    /// <summary>One option for a <see cref="ConfigFieldKind.Select"/> field.</summary>
    /// <param name="Value">The stored value (what lands in <c>ConfigJson</c>).</param>
    /// <param name="Label">The label shown in the dropdown.</param>
    public sealed record ConfigSelectOption(string Value, string Label);
}
