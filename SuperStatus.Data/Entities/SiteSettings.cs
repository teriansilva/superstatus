namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Issue #167: the single-row store for operator-editable branding — the
    /// site title, logo URL, and HUD accent color. Treated as a singleton
    /// (fixed <see cref="SingletonId"/>); the service get-or-creates that row
    /// and seeds it once from config on first run, so later config reloads never
    /// overwrite operator edits. The footer (#170) and AI (#168) features extend
    /// this row with their own columns in their own migrations.
    /// </summary>
    public class SiteSettings : EntityBase
    {
        /// <summary>The one and only settings row id.</summary>
        public const long SingletonId = 1;

        /// <summary>Brand wordmark in the topbar (e.g. "TEST"). Blank keeps the
        /// stylized SUPER//STATUS mark.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Issue #175: brand subtitle line beside the wordmark. Seeded to
        /// "Status monitoring" on first run (SiteSettingsService.DefaultSubtitle); if
        /// an operator clears it, the header falls back to SuperStatusConfig.Description.</summary>
        public string Subtitle { get; set; } = string.Empty;

        /// <summary>Optional brand logo (http/https URL only). Empty = no logo.</summary>
        public string LogoUrl { get; set; } = string.Empty;

        /// <summary>HUD accent color as #rrggbb. Drives --accent (+ derived soft/glow);
        /// semantic status colors are independent and never change.</summary>
        public string AccentColor { get; set; } = string.Empty;

        /// <summary>Issue #170: static footer line (replaces the #109 rotating
        /// classification). Seeded from the prior footer text on first run.</summary>
        public string FooterText { get; set; } = string.Empty;

        /// <summary>Issue #170: footer links as a JSON array of {label,url}.
        /// Stored as JSON so the singleton stays one row; the service
        /// (de)serializes to <see cref="ViewModels.FooterLink"/>.</summary>
        public string FooterLinksJson { get; set; } = "[]";

        /// <summary>Issue #170: whether the footer shows the Admin/console link
        /// (default on). The /admin route stays reachable + [Authorize] either way.</summary>
        public bool ShowAdminLink { get; set; } = true;

        /// <summary>When the settings were last saved.</summary>
        public DateTime UpdatedUtc { get; set; }

        /// <summary>Issue #181: when the first-run setup wizard was completed.
        /// Null = onboarding not finished → the admin console renders the wizard
        /// instead of the operator surface. Set once by the wizard's Finish step.</summary>
        public DateTime? OnboardedUtc { get; set; }

        // ---- Issue #168: AI / automation settings ----

        /// <summary>Master switch for AI-authored incidents. When false the
        /// sustained-downtime trigger never calls the model (and falls back to a
        /// templated incident if a per-check toggle is on).</summary>
        public bool AiEnabled { get; set; }

        /// <summary>OpenAI-compatible API root (e.g. https://gateway/v1); the draft
        /// service appends /chat/completions. http(s) only.</summary>
        public string AiBaseUrl { get; set; } = string.Empty;

        /// <summary>Model name passed to the chat-completions call.</summary>
        public string AiModel { get; set; } = string.Empty;

        /// <summary>API key (Bearer). Stored plaintext for now — encrypted-at-rest
        /// is a tracked follow-up (#168). Never echoed back over the API: GET
        /// surfaces only <see cref="ViewModels.SiteSettingsViewModel.AiApiKeySet"/>.</summary>
        public string AiApiKey { get; set; } = string.Empty;

        /// <summary>Per-request inference timeout in seconds (bounded 5–120).</summary>
        public int AiTimeoutSeconds { get; set; } = 20;

        /// <summary>Global default: how long a check must stay down before an
        /// auto-incident is drafted (minutes, bounded 1–1440).</summary>
        public int AutoIncidentThresholdMinutes { get; set; } = 5;

        /// <summary>Operator-editable prompt template for the incident draft. Blank
        /// falls back to the service's built-in default prompt. Supports the
        /// {service} {url} {downSince} {minutes} {symptom} placeholders; the draft
        /// service constrains the model to return JSON regardless.</summary>
        public string AiPromptTemplate { get; set; } = string.Empty;
    }
}
