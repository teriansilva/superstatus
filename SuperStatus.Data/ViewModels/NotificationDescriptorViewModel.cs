namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// #343 Phase 2. Wire shape of a notification channel's descriptor — the delivery
    /// sibling of <see cref="ProviderDescriptorViewModel"/>. The API projects each
    /// registered channel to this and the Plugins page renders the "Notification
    /// channels" catalogue from it. Lives in Data so both the ApiService (serialize)
    /// and the Web (deserialize) share one contract; the Web does not reference the
    /// Services project where the channels themselves live.
    /// <para>
    /// Phase 2 carries display metadata only — no config <c>Fields</c> (per-profile
    /// channel config is Phase 3), so the anonymous descriptor endpoint exposes no
    /// secret surface by construction.
    /// </para>
    /// </summary>
    public sealed class NotificationDescriptorViewModel
    {
        public string TypeId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;

        /// <summary>One operator-facing sentence on what this channel does — rendered
        /// verbatim (no page-local channel prose).</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Whether the channel can send a test message on demand (email yes,
        /// web push no). Drives the read-only capability tag on the Plugins page.</summary>
        public bool SupportsTest { get; set; }

        /// <summary>Plugin category — always <see cref="PluginCategories.Notification"/>
        /// for a channel. Present so the page groups on a real field.</summary>
        public string Category { get; set; } = PluginCategories.Notification;

        /// <summary>#343 Phase 5: the channel's config-schema field declarations — reuses
        /// the check-provider wire type (the vocabulary is shared). Empty for channels with
        /// no per-profile config (web push). Declarations only: a <c>secret</c> field's
        /// stored value is never emitted here.</summary>
        public List<ProviderConfigFieldViewModel> Fields { get; set; } = new();
    }
}
