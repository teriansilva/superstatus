namespace SuperStatus.Services.Plugins
{
    /// <summary>
    /// Epic #271 / #312 Phase 1 (relocated in #361 Phase 5 to the seam-neutral
    /// <c>SuperStatus.Services.Plugins</c> namespace, shared by the check + notification
    /// provider seams). The <b>closed</b> field vocabulary a provider's
    /// <see cref="ConfigSchema"/> may use. The schema-driven edit dialog renders any
    /// provider's form generically from these kinds — there is deliberately no
    /// arbitrary form engine. The vocabulary grows only when a real provider needs a
    /// new kind.
    /// </summary>
    public enum ConfigFieldKind
    {
        /// <summary>Free single-line text (e.g. a URL).</summary>
        Text = 0,

        /// <summary>An integer value (rendered as a numeric field).</summary>
        Number = 1,

        /// <summary>A boolean toggle.</summary>
        Bool = 2,

        /// <summary>A credential. Masked on read, write-only, preserved on blank
        /// re-save, never serialized into the public API or dashboard — the same
        /// rule the SMTP/AI key already follow (see <c>SiteSettingsService</c>).</summary>
        Secret = 3,

        /// <summary>One value chosen from a fixed option list
        /// (<see cref="ConfigField.Options"/>).</summary>
        Select = 4,
    }
}
