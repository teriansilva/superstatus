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

        // ---- Issue #241 Phase B: SMTP / email alerts ----

        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;
        public bool SmtpUseStartTls { get; set; } = true;
        public string SmtpUsername { get; set; } = string.Empty;

        /// <summary>SMTP password — WRITE-ONLY. On GET always null; send a non-empty
        /// value to set/replace, or leave null/empty to preserve the stored one.</summary>
        public string? SmtpPassword { get; set; }

        /// <summary>GET-only — whether an SMTP password is stored. Ignored on save.</summary>
        public bool SmtpPasswordSet { get; set; }

        public string SmtpFromAddress { get; set; } = string.Empty;
        public string SmtpFromName { get; set; } = string.Empty;
        public string AlertDefaultRecipients { get; set; } = string.Empty;

        /// <summary>GET-only — when the SMTP config was last verified by a successful
        /// "send test"; null if never or if a transport field changed since. Ignored on save.</summary>
        public DateTime? SmtpVerifiedUtc { get; set; }

        // ---- Issue #358: self-host dynamic-issuer allowlist ----

        /// <summary>The operator-configured allowed sign-in hosts (each a <c>host</c>
        /// or <c>host:port</c>). Empty ⇒ relaxed (self-host first run accepts any
        /// request host); non-empty ⇒ hardened (only these are honored as the login
        /// issuer). Surfaced on GET (non-secret) so the console can drive the
        /// security banner + editor; written via the dedicated <c>/settings/authhosts</c>
        /// save so an unrelated panel edit can't clear it.</summary>
        public List<string> AllowedAuthHosts { get; set; } = new();
    }
}
