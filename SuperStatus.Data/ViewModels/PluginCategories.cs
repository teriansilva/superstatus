namespace SuperStatus.Data.ViewModels
{
    /// <summary>
    /// #343 Phase 2: the plugin-catalogue categories the Plugins page groups by. A
    /// stable wire vocabulary (like the descriptor's <c>direction</c>/<c>kind</c>
    /// strings) shared by both descriptor endpoints so the page groups on a real
    /// field, not an implicit endpoint split.
    /// </summary>
    public static class PluginCategories
    {
        /// <summary>A check provider (something to monitor) — <c>/statuscheck/providers</c>.</summary>
        public const string Check = "check";

        /// <summary>A notification channel (how an alert is delivered) — <c>/notifications/providers</c>.</summary>
        public const string Notification = "notification";
    }
}
