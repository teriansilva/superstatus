using System.Text.Json;
using System.Text.RegularExpressions;
using SuperStatus.Configuration;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Services.Services
{
    public interface ISiteSettingsService
    {
        /// <summary>Returns the singleton settings, seeding the row once from config on first run.</summary>
        Task<SiteSettingsViewModel> GetSettingsAsync(CancellationToken cancellationToken = default);

        /// <summary>Validates + persists the singleton settings (operator action).</summary>
        Task<SiteSettingsViewModel> SaveSettingsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default);

        /// <summary>#181: mark the first-run setup wizard complete (idempotent — only
        /// stamps OnboardedUtc the first time). Returns the updated settings.</summary>
        Task<SiteSettingsViewModel> CompleteOnboardingAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Issue #167/#170. Operator-editable branding + footer over the
    /// <see cref="SiteSettings"/> singleton. The row is get-or-created at
    /// <see cref="SiteSettings.SingletonId"/> and seeded ONCE from config on first
    /// run, so later config reloads never overwrite operator edits. Inputs are
    /// validated/sanitized: accent must be #rrggbb (else the HUD default), logo +
    /// footer-link URLs must be http/https (else cleared/dropped), and footer
    /// links are capped at <see cref="MaxFooterLinks"/>.
    /// </summary>
    public class SiteSettingsService(ISiteSettingsRepository repository) : ISiteSettingsService
    {
        /// <summary>The HUD design-system accent (hud-theme.css --accent) — the seed + invalid-input fallback.</summary>
        public const string DefaultAccent = "#3fbf6f";

        /// <summary>First-run brand wordmark. "SuperStatus" keeps the stylized
        /// SUPER//STATUS mark (HeaderBar treats it as the default), so a fresh
        /// install is product-branded out of the box — never seeded from a
        /// deployment-specific config value.</summary>
        public const string DefaultTitle = "SuperStatus";

        /// <summary>First-run brand subtitle beside the wordmark.</summary>
        public const string DefaultSubtitle = "Status monitoring";

        /// <summary>Seed footer line — the prior static classification text, so the
        /// footer is visually unchanged on first deploy (#170).</summary>
        public const string DefaultFooterText = "UNCLASSIFIED // INTERNAL USE";

        /// <summary>Bound the footer link list so a runaway payload can't bloat the row.</summary>
        public const int MaxFooterLinks = 8;

        // Issue #168: AI / automation bounds + defaults.
        public const int DefaultAiTimeoutSeconds = 20;
        public const int MinAiTimeoutSeconds = 5;
        public const int MaxAiTimeoutSeconds = 120;
        public const int DefaultThresholdMinutes = 5;
        public const int MinThresholdMinutes = 1;
        public const int MaxThresholdMinutes = 1440;

        /// <summary>Cap the editable prompt so a runaway paste can't bloat the row.</summary>
        public const int MaxAiPromptLength = 4000;

        /// <summary>Built-in incident-draft prompt used when the operator hasn't set
        /// their own. Constrains the model to return only JSON; placeholders
        /// ({service} {url} {downSince} {minutes} {symptom}) are filled by the draft
        /// service (Phase 2). Exposed + editable in the operator console.</summary>
        public const string DefaultAiPrompt =
            "You are writing a public status-page incident notice for a monitored service that is currently DOWN. " +
            "Use ONLY the facts provided — never invent a root cause. " +
            "Respond with ONLY a JSON object, no prose, no markdown fences: " +
            "{\"title\": string (<= 80 chars), \"description\": string (<= 400 chars, plain text), " +
            "\"severity\": one of \"Minor\", \"Severe\", \"Critical\"}.\n" +
            "Service: {service}\nURL: {url}\nDown since: {downSince} ({minutes} minutes)\nLast observed symptom: {symptom}";

        private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public async Task<SiteSettingsViewModel> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);
            return ToViewModel(row);
        }

        public async Task<SiteSettingsViewModel> SaveSettingsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
        {
            var row = await repository.GetSingletonAsync(cancellationToken);
            bool isNew = row is null;
            row ??= new SiteSettings { Id = SiteSettings.SingletonId };

            row.Title = (settings.Title ?? string.Empty).Trim();
            row.Subtitle = (settings.Subtitle ?? string.Empty).Trim();
            row.LogoUrl = SanitizeHttpUrl(settings.LogoUrl);
            row.AccentColor = NormalizeAccent(settings.AccentColor);
            row.FooterText = (settings.FooterText ?? string.Empty).Trim();
            row.FooterLinksJson = JsonSerializer.Serialize(SanitizeLinks(settings.FooterLinks), Json);
            row.ShowAdminLink = settings.ShowAdminLink;

            // #168: AI / automation. The key is WRITE-ONLY — a null/blank submission
            // preserves the stored key (the UI never receives it back), a non-blank
            // value replaces it. AI only stays enabled with a usable config (valid
            // http(s) base URL + non-empty model) so the trigger never calls a
            // half-configured endpoint; the API key is optional (some gateways need
            // none). Timeout + threshold are clamped to sane bounds.
            if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
                row.AiApiKey = settings.AiApiKey.Trim();
            row.AiBaseUrl = SanitizeHttpUrl(settings.AiBaseUrl);
            row.AiModel = (settings.AiModel ?? string.Empty).Trim();
            row.AiTimeoutSeconds = ClampOrDefault(settings.AiTimeoutSeconds, MinAiTimeoutSeconds, MaxAiTimeoutSeconds, DefaultAiTimeoutSeconds);
            row.AutoIncidentThresholdMinutes = ClampOrDefault(settings.AutoIncidentThresholdMinutes, MinThresholdMinutes, MaxThresholdMinutes, DefaultThresholdMinutes);
            // Prompt: trim + cap; a value equal to the default (or blank) stores blank
            // so the row tracks the evolving built-in default rather than freezing a copy.
            row.AiPromptTemplate = NormalizePrompt(settings.AiPromptTemplate);
            row.AiEnabled = settings.AiEnabled && row.AiBaseUrl.Length > 0 && row.AiModel.Length > 0;

            row.UpdatedUtc = DateTime.UtcNow;

            row = isNew
                ? await repository.AddAndSave(row, cancellationToken)
                : await repository.UpdateAndSave(row, cancellationToken);
            return ToViewModel(row);
        }

        public async Task<SiteSettingsViewModel> CompleteOnboardingAsync(CancellationToken cancellationToken = default)
        {
            var row = await repository.GetSingletonAsync(cancellationToken);
            bool isNew = row is null;
            row ??= new SiteSettings { Id = SiteSettings.SingletonId };

            // Idempotent: only stamp the first time so a re-run can't move the date.
            row.OnboardedUtc ??= DateTime.UtcNow;

            row = isNew
                ? await repository.AddAndSave(row, cancellationToken)
                : await repository.UpdateAndSave(row, cancellationToken);
            return ToViewModel(row);
        }

        // First-run seed from config. Fixed Id so a second "global" row can never appear.
        private async Task<SiteSettings> SeedAsync(CancellationToken cancellationToken)
        {
            var seeded = new SiteSettings
            {
                Id = SiteSettings.SingletonId,
                Title = DefaultTitle,
                Subtitle = DefaultSubtitle,
                LogoUrl = SanitizeHttpUrl(SuperStatusConfig.LogoUrl),
                AccentColor = DefaultAccent,
                FooterText = DefaultFooterText,
                FooterLinksJson = "[]",
                ShowAdminLink = true,
                UpdatedUtc = DateTime.UtcNow,
            };
            return await repository.AddAndSave(seeded, cancellationToken);
        }

        private static SiteSettingsViewModel ToViewModel(SiteSettings s) => new()
        {
            Title = s.Title,
            Subtitle = s.Subtitle,
            LogoUrl = s.LogoUrl,
            AccentColor = string.IsNullOrWhiteSpace(s.AccentColor) ? DefaultAccent : s.AccentColor,
            FooterText = s.FooterText,
            // Re-sanitize on read too: a malformed/hand-edited row (or any writer
            // that bypasses SaveSettingsAsync) must never surface a non-http(s)
            // link in the anonymous public footer.
            FooterLinks = SanitizeLinks(DeserializeLinks(s.FooterLinksJson)),
            ShowAdminLink = s.ShowAdminLink,
            OnboardedUtc = s.OnboardedUtc,
            // #168: AI / automation. The key is never echoed — only whether one is set.
            AiEnabled = s.AiEnabled,
            AiBaseUrl = s.AiBaseUrl,
            AiModel = s.AiModel,
            AiApiKey = null,
            AiApiKeySet = !string.IsNullOrEmpty(s.AiApiKey),
            AiTimeoutSeconds = s.AiTimeoutSeconds <= 0 ? DefaultAiTimeoutSeconds : s.AiTimeoutSeconds,
            AutoIncidentThresholdMinutes = s.AutoIncidentThresholdMinutes <= 0 ? DefaultThresholdMinutes : s.AutoIncidentThresholdMinutes,
            // Surface the EFFECTIVE prompt so the operator sees + can tweak exactly
            // what the model is asked (blank row → the built-in default).
            AiPromptTemplate = string.IsNullOrWhiteSpace(s.AiPromptTemplate) ? DefaultAiPrompt : s.AiPromptTemplate,
        };

        /// <summary>Clamp to [min,max]; a non-positive (unset) value falls back to the default.</summary>
        private static int ClampOrDefault(int value, int min, int max, int fallback)
            => value <= 0 ? fallback : Math.Clamp(value, min, max);

        /// <summary>Trim + length-cap the prompt. A blank value, or one identical to
        /// the built-in default, is stored as empty so the row follows the evolving
        /// default instead of freezing a copy.</summary>
        private static string NormalizePrompt(string? value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value == DefaultAiPrompt) return string.Empty;
            return value.Length > MaxAiPromptLength ? value[..MaxAiPromptLength] : value;
        }

        /// <summary>Keep only links with a non-empty label + an http(s) URL, capped
        /// at <see cref="MaxFooterLinks"/>. Labels are trimmed; bad URLs drop the link.</summary>
        private static List<FooterLink> SanitizeLinks(IEnumerable<FooterLink>? links)
        {
            if (links is null) return new();
            return links
                .Select(l => new FooterLink { Label = (l.Label ?? string.Empty).Trim(), Url = SanitizeHttpUrl(l.Url) })
                .Where(l => l.Label.Length > 0 && l.Url.Length > 0)
                .Take(MaxFooterLinks)
                .ToList();
        }

        // Tolerant of a malformed/empty stored value — never throws into the render path.
        private static List<FooterLink> DeserializeLinks(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonSerializer.Deserialize<List<FooterLink>>(json, Json) ?? new(); }
            catch (JsonException) { return new(); }
        }

        /// <summary>Accept #rrggbb (lower-cased); anything else → the HUD default. The
        /// configurable accent never touches the semantic status tokens.</summary>
        private static string NormalizeAccent(string? value)
        {
            value = (value ?? string.Empty).Trim();
            return HexColor.IsMatch(value) ? value.ToLowerInvariant() : DefaultAccent;
        }

        /// <summary>Empty, or an http/https absolute URL. Rejects javascript:/data:/other schemes.
        /// Shared by the logo and footer-link URLs.</summary>
        private static string SanitizeHttpUrl(string? value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value
                : string.Empty;
        }
    }
}
