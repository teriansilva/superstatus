using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Notifications;
using SuperStatus.Services.Plugins;

namespace SuperStatus.ApiService;

/// <summary>
/// #343 Phase 2. Exposes the registered notification channels (their static display
/// descriptors) so the Web Plugins page can render the "Notification channels"
/// catalogue alongside the check providers. The delivery sibling of
/// <see cref="CheckProviderApi"/>. The channels themselves live in the Services project
/// (which the Web does not reference); this is the single, server-driven source.
/// </summary>
public static class NotificationProviderApi
{
    public static void MapNotificationProviderApi(this IEndpointRouteBuilder app)
    {
        // Anonymous read — channel descriptors are static, non-sensitive display
        // metadata (id / name / icon / one-sentence blurb / test capability) plus, from
        // #343 Phase 5, the config-field DECLARATIONS (key / label / kind / options). A
        // secret field's stored VALUE is never projected here — only that the field exists
        // — so no secret surface is exposed by construction.
        app.MapGet("/notifications/providers", (INotificationProviderRegistry registry) =>
            Results.Ok(registry.Descriptors.Select(ToViewModel).ToList()));
    }

    public static NotificationDescriptorViewModel ToViewModel(NotificationDescriptor d) => new()
    {
        TypeId = d.TypeId,
        DisplayName = d.DisplayName,
        Icon = d.Icon,
        Description = d.Description,
        SupportsTest = d.SupportsTest,
        Category = PluginCategories.Notification,
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
    };
}
