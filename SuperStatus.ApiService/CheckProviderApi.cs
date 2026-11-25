using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Plugins;

namespace SuperStatus.ApiService;

/// <summary>
/// Epic #271 / #312 Phase 1. Exposes the registered check providers + their config
/// schemas so the Web edit dialog can render the Type selector and the generic
/// (schema-driven) config form. The providers themselves live in the Services project
/// (which the Web does not reference); this is the single, server-driven source.
/// </summary>
public static class CheckProviderApi
{
    public static void MapCheckProviderApi(this IEndpointRouteBuilder app)
    {
        // Anonymous read — provider descriptors are static, non-sensitive metadata
        // (field labels / kinds / options). No secret VALUES are ever exposed here;
        // secret fields are only declared in the schema, never their stored contents.
        app.MapGet("/statuscheck/providers", (ICheckProviderRegistry registry) =>
            Results.Ok(registry.Descriptors.Select(ToViewModel).ToList()));
    }

    public static ProviderDescriptorViewModel ToViewModel(ProviderDescriptor d) => new()
    {
        TypeId = d.TypeId,
        DisplayName = d.DisplayName,
        Icon = d.Icon,
        SchemaVersion = d.ConfigSchema.Version,
        Description = d.Description,
        Direction = d.Direction.ToString().ToLowerInvariant(),
        Category = PluginCategories.Check,
        BatchTargetField = d.BatchTargetField,
        Fields = d.ConfigSchema.Fields.Select(f => new ProviderConfigFieldViewModel
        {
            Key = f.Key,
            Label = f.Label,
            Kind = f.Kind.ToString().ToLowerInvariant(),
            Required = f.Required,
            Help = f.Help,
            Placeholder = f.Placeholder,
            Options = (f.Options ?? Array.Empty<ConfigSelectOption>())
                .Select(o => new ProviderConfigOptionViewModel { Value = o.Value, Label = o.Label })
                .ToList(),
        }).ToList(),
        Metrics = d.MetricDefs.Select(m => new ProviderMetricDefViewModel
        {
            Key = m.Key,
            Label = m.Label,
            Unit = m.Unit,
            Kind = m.Kind.ToString().ToLowerInvariant(),
            WarnThreshold = m.WarnThreshold,
            CritThreshold = m.CritThreshold,
        }).ToList(),
    };
}
