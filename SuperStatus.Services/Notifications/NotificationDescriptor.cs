using SuperStatus.Services.Plugins;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// Phase 1 of #343. The static description of a notification channel — the delivery
/// sibling of <c>SuperStatus.Services.Providers.ProviderDescriptor</c>. Carries the
/// stable <see cref="TypeId"/>, a display name + icon token for the Plugins page, a
/// one-sentence <see cref="Description"/>, whether the channel supports a test send
/// (<see cref="SupportsTest"/>), and — #343 Phase 5 — a versioned <see cref="ConfigSchema"/>
/// that drives the generic per-channel config form (empty for channels with no config,
/// e.g. web push).
/// </summary>
public sealed class NotificationDescriptor
{
    /// <summary>The shared empty schema for channels that need no per-profile config.</summary>
    public static readonly ConfigSchema NoConfig = new(1, System.Array.Empty<ConfigField>());

    public NotificationDescriptor(string typeId, string displayName, string icon, string? description = null, bool supportsTest = false, ConfigSchema? configSchema = null)
    {
        TypeId = typeId;
        DisplayName = displayName;
        Icon = icon;
        Description = description ?? string.Empty;
        SupportsTest = supportsTest;
        ConfigSchema = configSchema ?? NoConfig;
    }

    /// <summary>Stable channel id (e.g. <c>email</c>, <c>webpush</c>, <c>slack</c>).</summary>
    public string TypeId { get; }

    /// <summary>Label shown on the Plugins page (e.g. <c>Email (SMTP)</c>).</summary>
    public string DisplayName { get; }

    /// <summary>Icon hint — a token the UI maps to a glyph.</summary>
    public string Icon { get; }

    /// <summary>One operator-facing sentence on what this channel does.</summary>
    public string Description { get; }

    /// <summary>Whether the channel can send a test message on demand. A capability, not a
    /// contract requirement: the UI only offers "Send test" when this is true.</summary>
    public bool SupportsTest { get; }

    /// <summary>#343 Phase 5: the versioned config schema that drives the generic
    /// per-channel config form. <see cref="NoConfig"/> for channels with no config. The
    /// <see cref="ConfigSchema"/> vocabulary lives in the seam-neutral
    /// <c>SuperStatus.Services.Plugins</c> namespace, shared by the check + notification
    /// seams (relocated from <c>Services.Providers</c> in #361 Phase 5).</summary>
    public ConfigSchema ConfigSchema { get; }
}
