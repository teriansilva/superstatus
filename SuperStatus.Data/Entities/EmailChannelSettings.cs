using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// #343 Phase 3: the <c>email</c> channel's config, marshalled into
    /// <see cref="AlertProfileChannel.ConfigJson"/> — the recipients + the
    /// "use site default recipients" flag that used to live in the deprecated
    /// <see cref="AlertProfile"/> columns. Kept as a small typed shape so the engine
    /// and the admin API read/write one format (the generic schema-driven form the
    /// other channels will use lands with #343 Phase 5).
    /// </summary>
    public sealed record EmailChannelSettings(
        [property: JsonPropertyName("recipients")] string Recipients,
        [property: JsonPropertyName("usesSiteDefault")] bool UsesSiteDefault)
    {
        public static EmailChannelSettings Empty { get; } = new(string.Empty, false);

        public string ToJson() => JsonSerializer.Serialize(this);

        /// <summary>Parse the email channel's ConfigJson; tolerant of null/blank/malformed
        /// (returns <see cref="Empty"/>) so a bad row degrades to "no recipients" rather
        /// than throwing in the send path.</summary>
        public static EmailChannelSettings FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty;
            try
            {
                return JsonSerializer.Deserialize<EmailChannelSettings>(json) ?? Empty;
            }
            catch (JsonException)
            {
                return Empty;
            }
        }
    }
}
