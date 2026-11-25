using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// #343 Phase 4: the <c>webhook</c> channel's config, marshalled into
    /// <see cref="AlertProfileChannel.ConfigJson"/> — the target URL a folded webhook
    /// posts its alert payload to, plus an optional JSON payload template. The old
    /// per-webhook throttle is superseded by the profile's alert-rules throttle.
    /// </summary>
    public sealed record WebhookChannelSettings(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("payloadJson")] string PayloadJson = "")
    {
        public static WebhookChannelSettings Empty { get; } = new(string.Empty, string.Empty);

        public string ToJson() => JsonSerializer.Serialize(this);

        /// <summary>Parse the webhook channel's ConfigJson; tolerant of null/blank/malformed
        /// (returns <see cref="Empty"/>) so a bad row degrades to "no url" (a calm Skipped)
        /// rather than throwing in the send path.</summary>
        public static WebhookChannelSettings FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty;
            try
            {
                return JsonSerializer.Deserialize<WebhookChannelSettings>(json) ?? Empty;
            }
            catch (JsonException)
            {
                return Empty;
            }
        }
    }
}
