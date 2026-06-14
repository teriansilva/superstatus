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

        /// <summary>Issue #175: brand subtitle line beside the wordmark. #286: no
        /// default — not seeded on first run, and a blank value renders nothing (no
        /// config fallback). The header shows it only once the operator sets it.</summary>
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

        // ---- Issue #249 (epic #248): update-check state ----

        /// <summary>Whether the nightly update check runs (default on). The check is
        /// read-only — it never applies anything — so it's safe to leave enabled.</summary>
        public bool UpdateCheckEnabled { get; set; } = true;

        /// <summary>When the last update check completed (success or failure). Null = never checked.</summary>
        public DateTime? UpdateLastCheckedUtc { get; set; }

        /// <summary>Latest release version from the most recent SUCCESSFUL check
        /// (normalized SemVer, no leading v). Kept as last-known-good across a later
        /// failed check (which only sets <see cref="UpdateLastCheckError"/>); null
        /// only until the first successful check.</summary>
        public string? UpdateLatestVersion { get; set; }

        /// <summary>Release-notes URL for <see cref="UpdateLatestVersion"/> (GitHub
        /// release page). Updated/retained alongside it on success.</summary>
        public string? UpdateLatestNotesUrl { get; set; }

        /// <summary>Why the last check couldn't complete (network / rate-limit / parse).
        /// Null when the last check succeeded; surfaced as a calm "couldn't check" state.</summary>
        public string? UpdateLastCheckError { get; set; }

        // ---- Issue #241 Phase B: SMTP / email-alert settings ----

        /// <summary>SMTP server host. Email alerts send only when this + a from-address
        /// are set (and a per-check or default recipient exists).</summary>
        public string SmtpHost { get; set; } = string.Empty;

        /// <summary>SMTP port (e.g. 587 STARTTLS, 465 implicit TLS, 25 plain).</summary>
        public int SmtpPort { get; set; } = 587;

        /// <summary>Use STARTTLS (true, port 587) vs implicit TLS / plain (false). The
        /// notifier upgrades opportunistically; 465 is treated as implicit TLS.</summary>
        public bool SmtpUseStartTls { get; set; } = true;

        /// <summary>SMTP auth username. Blank = no authentication (open relay on a LAN).</summary>
        public string SmtpUsername { get; set; } = string.Empty;

        /// <summary>SMTP auth password. Stored plaintext for now (encrypt-at-rest is a
        /// tracked follow-up); WRITE-ONLY over the API — GET surfaces only
        /// <see cref="ViewModels.SiteSettingsViewModel.SmtpPasswordSet"/>.</summary>
        public string SmtpPassword { get; set; } = string.Empty;

        /// <summary>The envelope/from address alerts are sent from.</summary>
        public string SmtpFromAddress { get; set; } = string.Empty;

        /// <summary>Optional display name for the from address.</summary>
        public string SmtpFromName { get; set; } = string.Empty;

        /// <summary>Comma/space/semicolon-separated default recipients used when a
        /// check has email alerts on but no per-check recipients.</summary>
        public string AlertDefaultRecipients { get; set; } = string.Empty;

        /// <summary>When the SMTP config was last verified by a successful "send test"
        /// (informational). CLEARED whenever a transport-affecting field changes, so a
        /// stale success can never imply the current relay works.</summary>
        public DateTime? SmtpVerifiedUtc { get; set; }

        // ---- Issue #241 Phase C: Web Push (VAPID) ----

        /// <summary>VAPID public key (base64url). Handed to the browser so it can create a
        /// push subscription bound to this server. Generated once, lazily, the first time
        /// a device subscribes; non-secret (the browser sends it to the push service).</summary>
        public string VapidPublicKey { get; set; } = string.Empty;

        /// <summary>VAPID private key (base64url). Signs the push requests. SECRET — never
        /// echoed over the API (not surfaced on the settings view-model). Generated as a
        /// pair with <see cref="VapidPublicKey"/>.</summary>
        public string VapidPrivateKey { get; set; } = string.Empty;
    }
}
