using System.Collections.Generic;

namespace SuperStatus.Data.ViewModels
{
    /// <summary>Issue #170: a single footer link (label + absolute http(s) URL).</summary>
    public class FooterLink
    {
        public string Label { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// Issue #167/#170: operator-editable site settings — the public-readable /
    /// operator-writable shape over the <see cref="Entities.SiteSettings"/>
    /// singleton (branding + footer).
    /// </summary>
    public class SiteSettingsViewModel
    {
        public string Title { get; set; } = string.Empty;
        /// <summary>#175: brand subtitle beside the wordmark (empty → config default).</summary>
        public string Subtitle { get; set; } = string.Empty;
        public string LogoUrl { get; set; } = string.Empty;
        /// <summary>HUD accent as #rrggbb.</summary>
        public string AccentColor { get; set; } = string.Empty;

        /// <summary>#170: static footer line (replaces the rotating classification).</summary>
        public string FooterText { get; set; } = string.Empty;

        /// <summary>#170: footer links (label + http(s) URL).</summary>
        public List<FooterLink> FooterLinks { get; set; } = new();

        /// <summary>#170: show the footer Admin/console link (default on).</summary>
        public bool ShowAdminLink { get; set; } = true;

        /// <summary>#181: when first-run setup was completed (null = wizard still pending).</summary>
        public DateTime? OnboardedUtc { get; set; }

        // ---- Issue #168: AI / automation ----

        /// <summary>#168: master switch for AI-authored incidents.</summary>
        public bool AiEnabled { get; set; }

        /// <summary>#168: OpenAI-compatible API root (http(s); /chat/completions appended).</summary>
        public string AiBaseUrl { get; set; } = string.Empty;

        /// <summary>#168: chat-completions model name.</summary>
        public string AiModel { get; set; } = string.Empty;

        /// <summary>#168: API key — WRITE-ONLY. On GET this is always null (never
        /// echoed); send a non-empty value to set/replace it, or leave null/empty
        /// on save to preserve the stored key. See <see cref="AiApiKeySet"/>.</summary>
        public string? AiApiKey { get; set; }

        /// <summary>#168: GET-only — whether a key is currently stored (so the UI can
        /// show "key set" without revealing it). Ignored on save.</summary>
        public bool AiApiKeySet { get; set; }

        /// <summary>#168: per-request inference timeout (seconds, bounded 5–120).</summary>
        public int AiTimeoutSeconds { get; set; } = 20;

        /// <summary>#168: global default sustained-downtime threshold (minutes, bounded 1–1440).</summary>
        public int AutoIncidentThresholdMinutes { get; set; } = 5;

        /// <summary>#168: operator-editable AI prompt template. GET returns the
        /// effective prompt (the stored value, or the built-in default when blank)
        /// so the operator can see + tweak exactly what the model is asked. Supports
        /// {service} {url} {downSince} {minutes} {symptom} placeholders.</summary>
        public string AiPromptTemplate { get; set; } = string.Empty;
    }
}
