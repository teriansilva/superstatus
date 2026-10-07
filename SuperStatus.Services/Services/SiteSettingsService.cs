using System.Text.Json;
using System.Text.RegularExpressions;
using SuperStatus.Configuration;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.ServiceDefaults;
using SuperStatus.Services.Updates;

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

        /// <summary>#241 Phase B: persist ONLY the SMTP/email-alert columns (its own
        /// endpoint, so a branding/AI save can't wipe a configured relay). Password is
        /// write-only (blank preserves on the same target); a transport retarget clears
        /// SmtpVerifiedUtc and — if no fresh password is given — clears the stored
        /// password so an old credential is never carried to a different host.</summary>
        Task<SiteSettingsViewModel> SaveSmtpSettingsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default);

        /// <summary>#358: persist ONLY the allowed-sign-in-hosts allowlist (its own
        /// endpoint, so a branding/AI/SMTP save can never clear this security-sensitive
        /// field). Entries are normalized/deduped/capped by
        /// <see cref="AuthHostAllowlist.Sanitize"/>. Returns the updated settings.</summary>
        Task<SiteSettingsViewModel> SaveAuthHostsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default);

        /// <summary>#249: read the persisted update-check state (the worker honours
        /// <c>Enabled</c>; the console renders the rest).</summary>
        Task<UpdateCheckStateDto> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default);

        /// <summary>#249: persist the result of an update check. Touches ONLY the
        /// update columns (never branding/AI), so the read-only worker can't trip
        /// the operator-input validation in <see cref="SaveSettingsAsync"/>.</summary>
        Task SetUpdateCheckResultAsync(string? latestVersion, string? latestNotesUrl, string? error, DateTime checkedUtc, CancellationToken cancellationToken = default);

        /// <summary>#334: read the operator's automatic-update policy (honoured by
        /// <c>AutoUpdateWorker</c>; rendered by the console's Updates panel).</summary>
        Task<AutoUpdateSettingsDto> GetAutoUpdateSettingsAsync(CancellationToken cancellationToken = default);

        /// <summary>#334: persist the auto-update toggle + daily UTC time (operator
        /// action from the Updates panel). Touches ONLY those two columns — never
        /// branding/AI/SMTP, and never <c>AutoUpdateLastRunUtc</c>. Returns the fresh
        /// policy.</summary>
        Task<AutoUpdateSettingsDto> SetAutoUpdateSettingsAsync(bool enabled, TimeOnly timeUtc, CancellationToken cancellationToken = default);

        /// <summary>#334: stamp the moment an automatic update was ACCEPTED by the
        /// updater. Called only on <see cref="UpdateTriggerOutcome.Accepted"/>, so a
        /// failed trigger leaves the day's slot open for a retry.</summary>
        Task MarkAutoUpdateRunAsync(DateTime whenUtc, CancellationToken cancellationToken = default);

        /// <summary>#241 Phase C: return the VAPID public key for browser push
        /// subscription, generating + persisting the key pair once on first use. The
        /// public key is non-secret; the private key never leaves the server. Concurrency
        /// safe — first-use generation is an atomic compare-and-set, so racing callers all
        /// receive the one persisted pair rather than clobbering each other.</summary>
        Task<string> GetOrCreateVapidPublicKeyAsync(CancellationToken cancellationToken = default);
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

        /// <summary>#286: no default subtitle seeded on first run — the header shows
        /// a subtitle only once the operator sets one (a blank subtitle renders
        /// nothing, with no config fallback).</summary>
        public const string DefaultSubtitle = "";

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

        // Issue #241 Phase B: SMTP port bounds + default.
        public const int DefaultSmtpPort = 587;
        public const int MinSmtpPort = 1;
        public const int MaxSmtpPort = 65535;

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

            // #241 Phase B: SMTP is persisted by SaveSmtpSettingsAsync (its own
            // endpoint), NOT here — so a branding/AI save from a panel that doesn't
            // carry the SMTP fields can't wipe a configured relay.

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

        public async Task<SiteSettingsViewModel> SaveSmtpSettingsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
        {
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);

            var newHost = (settings.SmtpHost ?? string.Empty).Trim();
            var newPort = ClampOrDefault(settings.SmtpPort, MinSmtpPort, MaxSmtpPort, DefaultSmtpPort);
            var newUser = (settings.SmtpUsername ?? string.Empty).Trim();
            var newFrom = (settings.SmtpFromAddress ?? string.Empty).Trim();

            // A "retarget" is any transport change EXCEPT the password itself.
            bool retargeted =
                row.SmtpHost != newHost
                || row.SmtpPort != newPort
                || row.SmtpUseStartTls != settings.SmtpUseStartTls
                || row.SmtpUsername != newUser
                || row.SmtpFromAddress != newFrom;
            bool passwordProvided = !string.IsNullOrWhiteSpace(settings.SmtpPassword);

            row.SmtpHost = newHost;
            row.SmtpPort = newPort;
            row.SmtpUseStartTls = settings.SmtpUseStartTls;
            row.SmtpUsername = newUser;
            row.SmtpFromAddress = newFrom;
            row.SmtpFromName = (settings.SmtpFromName ?? string.Empty).Trim();
            row.AlertDefaultRecipients = (settings.AlertDefaultRecipients ?? string.Empty).Trim();

            if (passwordProvided)
                row.SmtpPassword = settings.SmtpPassword!.Trim();
            else if (retargeted)
                // Don't carry an old credential to a different relay — require a fresh one.
                row.SmtpPassword = string.Empty;
            // else (same target, blank password) → preserve the stored password.

            // Any transport/credential change invalidates a prior "send test" success.
            if (retargeted || passwordProvided)
                row.SmtpVerifiedUtc = null;

            row.UpdatedUtc = DateTime.UtcNow;
            await repository.UpdateAndSave(row, cancellationToken);
            return ToViewModel(row);
        }

        public async Task<SiteSettingsViewModel> SaveAuthHostsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
        {
            // #358: dedicated save path (like SMTP) so this security-sensitive field
            // can't be zeroed by an unrelated panel's branding/AI save. Sanitize
            // normalizes each entry, drops the unparseable, dedupes, and caps the count.
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);

            var hosts = AuthHostAllowlist.Sanitize(settings.AllowedAuthHosts);
            row.AllowedAuthHostsJson = JsonSerializer.Serialize(hosts, Json);
            row.UpdatedUtc = DateTime.UtcNow;

            await repository.UpdateAndSave(row, cancellationToken);
            return ToViewModel(row);
        }

        public async Task<UpdateCheckStateDto> GetUpdateCheckStateAsync(CancellationToken cancellationToken = default)
        {
            // Read-only: don't seed a row just to report state. A fresh install with
            // no settings row yet defaults to enabled + never-checked.
            var row = await repository.GetSingletonAsync(cancellationToken);
            if (row is null)
                return new UpdateCheckStateDto(Enabled: true, null, null, null, null);

            return new UpdateCheckStateDto(
                Enabled: row.UpdateCheckEnabled,
                LastCheckedUtc: row.UpdateLastCheckedUtc,
                LatestVersion: row.UpdateLatestVersion,
                LatestNotesUrl: row.UpdateLatestNotesUrl,
                LastCheckError: row.UpdateLastCheckError);
        }

        public async Task SetUpdateCheckResultAsync(string? latestVersion, string? latestNotesUrl, string? error, DateTime checkedUtc, CancellationToken cancellationToken = default)
        {
            // Seed the FULL singleton (branding/footer defaults) if it doesn't exist
            // yet, rather than persisting a sparse row. The update worker runs a cycle
            // at startup and can beat the operator's first /settings visit; a bare row
            // would suppress the first-run seed and lose the branding defaults (#251).
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);

            row.UpdateLastCheckedUtc = checkedUtc;
            // On a failed check keep the last-known-good latest version but record the
            // error; on success clear the error.
            if (string.IsNullOrEmpty(error))
            {
                row.UpdateLatestVersion = latestVersion;
                row.UpdateLatestNotesUrl = latestNotesUrl;
                row.UpdateLastCheckError = null;
            }
            else
            {
                row.UpdateLastCheckError = error;
            }

            // The row already exists at this point (fetched or just seeded+saved),
            // so this is always an update of the update-check columns.
            await repository.UpdateAndSave(row, cancellationToken);
        }

        public async Task<AutoUpdateSettingsDto> GetAutoUpdateSettingsAsync(CancellationToken cancellationToken = default)
        {
            // Read-only: don't seed a row just to report policy. A fresh install with
            // no settings row yet reports the entity defaults (off, 03:00 UTC, never run).
            var row = await repository.GetSingletonAsync(cancellationToken);
            if (row is null)
                return AutoUpdateSettingsDto.Default;

            return new AutoUpdateSettingsDto(row.AutoUpdateEnabled, row.AutoUpdateTimeUtc, row.AutoUpdateLastRunUtc);
        }

        public async Task<AutoUpdateSettingsDto> SetAutoUpdateSettingsAsync(bool enabled, TimeOnly timeUtc, CancellationToken cancellationToken = default)
        {
            // Seed the FULL singleton if absent, for the same reason as
            // SetUpdateCheckResultAsync: a bare row would suppress the first-run
            // branding seed (#251).
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);

            row.AutoUpdateEnabled = enabled;
            row.AutoUpdateTimeUtc = timeUtc;
            // AutoUpdateLastRunUtc is deliberately untouched: it records what actually
            // ran, and re-configuring the schedule is not a run.
            row.UpdatedUtc = DateTime.UtcNow;

            await repository.UpdateAndSave(row, cancellationToken);
            return new AutoUpdateSettingsDto(row.AutoUpdateEnabled, row.AutoUpdateTimeUtc, row.AutoUpdateLastRunUtc);
        }

        public async Task MarkAutoUpdateRunAsync(DateTime whenUtc, CancellationToken cancellationToken = default)
        {
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);

            row.AutoUpdateLastRunUtc = whenUtc;
            row.UpdatedUtc = DateTime.UtcNow;

            await repository.UpdateAndSave(row, cancellationToken);
        }

        public async Task<string> GetOrCreateVapidPublicKeyAsync(CancellationToken cancellationToken = default)
        {
            var row = await repository.GetSingletonAsync(cancellationToken) ?? await SeedAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(row.VapidPublicKey) && !string.IsNullOrWhiteSpace(row.VapidPrivateKey))
                return row.VapidPublicKey;

            // First use: generate a pair and commit it with an atomic compare-and-set.
            // If two callers race, only the first's UPDATE matches the still-empty row;
            // the loser's matches nothing. Read the public key back (untracked, fresh) so
            // EVERY caller returns the pair that was actually persisted — never a
            // generated-but-discarded key a device could then subscribe against.
            var keys = WebPush.VapidHelper.GenerateVapidKeys();
            await repository.SetVapidKeysIfAbsentAsync(keys.PublicKey, keys.PrivateKey, DateTime.UtcNow, cancellationToken);
            var persisted = await repository.GetVapidPublicKeyAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(persisted) ? keys.PublicKey : persisted;
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
            // #241 Phase B: SMTP. Password is never echoed — only whether one is set.
            SmtpHost = s.SmtpHost,
            SmtpPort = s.SmtpPort <= 0 ? DefaultSmtpPort : s.SmtpPort,
            SmtpUseStartTls = s.SmtpUseStartTls,
            SmtpUsername = s.SmtpUsername,
            SmtpPassword = null,
            SmtpPasswordSet = !string.IsNullOrEmpty(s.SmtpPassword),
            SmtpFromAddress = s.SmtpFromAddress,
            SmtpFromName = s.SmtpFromName,
            AlertDefaultRecipients = s.AlertDefaultRecipients,
            SmtpVerifiedUtc = s.SmtpVerifiedUtc,
            // #358: allowed sign-in hosts — non-secret, always surfaced. Re-sanitize
            // on read so a hand-edited/malformed row can never emit a junk entry.
            AllowedAuthHosts = AuthHostAllowlist.Sanitize(DeserializeHosts(s.AllowedAuthHostsJson)),
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

        // #358: tolerant read of the allowed-auth-hosts JSON array — never throws.
        private static List<string> DeserializeHosts(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? new(); }
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
