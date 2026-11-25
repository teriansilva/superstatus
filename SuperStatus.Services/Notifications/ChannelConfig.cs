using System.Text.Json;
using SuperStatus.Data.Constants;

namespace SuperStatus.Services.Notifications;

/// <summary>
/// #343 Phase 5: shared helpers for the chat-channel providers (Slack / Discord /
/// Telegram) — read a string value out of the channel's stored <c>ConfigJson</c> (written
/// by the schema-driven form via <c>ProviderConfigWriter</c>, keyed by
/// <c>ConfigField.Key</c>), and build the human alert message from a
/// <see cref="NotificationContext"/>.
/// </summary>
internal static class ChannelConfig
{
    /// <summary>Read a string config value by key; tolerant of null/blank/malformed JSON
    /// (returns empty) so a bad row degrades to a calm Skipped rather than throwing.</summary>
    public static string Get(string? configJson, string key)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        return string.Empty;
    }

    /// <summary>The plain-text alert message the chat channels post — check title, the
    /// trigger, and the URL (recovery reads as a recovery).</summary>
    public static string Message(NotificationContext context)
    {
        var check = context.Check;
        bool recovered = context.Trigger == AlertTrigger.Recovery;
        string head = recovered
            ? $"Recovered: {check.Title}"
            : $"{Verb(context.Trigger)}: {check.Title}";
        string tail = recovered
            ? string.Empty
            : $"\nConsecutive failures: {check.ConsecutiveFailures}";
        return $"{head}\n{check.StatusCheckUrl}{tail}";
    }

    private static string Verb(AlertTrigger trigger) => trigger switch
    {
        AlertTrigger.Recovery => "RECOVERED",
        AlertTrigger.Outage => "OUTAGE",
        _ => "DOWN",
    };
}
