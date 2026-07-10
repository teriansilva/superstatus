using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services.Http;

namespace SuperStatus.Services.Services
{
    /// <summary>Issue #168: a drafted incident — AI-authored when the model is
    /// reachable, otherwise a templated fallback. <see cref="FromAi"/> records which.</summary>
    public sealed record IncidentDraft(string Title, string Description, IncidentSeverity Severity, bool FromAi);

    /// <summary>Issue #168: turns a sustained-downtime event into an incident draft.
    /// Calls the operator-configured OpenAI-compatible endpoint with a constrained
    /// prompt; on any failure (disabled / timeout / transport / parse) it returns a
    /// templated fallback so the outage is always recorded. The prompt carries only
    /// this instance's own check metadata + a fixed symptom phrase — never raw error
    /// text — so nothing sensitive can leak into public incident copy.</summary>
    public interface IIncidentDraftService
    {
        Task<IncidentDraft> DraftAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default);
    }

    public sealed class IncidentDraftService(
        ISiteSettingsRepository settingsRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<IncidentDraftService> logger) : IIncidentDraftService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // Title/description ceilings so a verbose model can't bloat a public incident.
        private const int MaxTitle = 120;
        private const int MaxDescription = 600;

        public async Task<IncidentDraft> DraftAsync(StatusCheck check, FailType failType, CancellationToken cancellationToken = default)
        {
            // Read the RAW settings row (not the masked view-model) so the write-only
            // API key is available to this trusted server-side path.
            var settings = await settingsRepository.GetSingletonAsync(cancellationToken);
            DateTime since = check.DownSinceUtc ?? DateTime.UtcNow;
            int minutes = Math.Max(0, (int)Math.Round((DateTime.UtcNow - since).TotalMinutes));
            string symptom = SymptomPhrase(failType);
            // Never let credentials in the monitored URL (userinfo or query tokens)
            // reach the AI provider or the PUBLIC incident copy — scheme/host/path only.
            string safeUrl = SanitizeUrl(check.StatusCheckUrl);

            bool aiUsable = settings is { AiEnabled: true }
                            && !string.IsNullOrWhiteSpace(settings.AiBaseUrl)
                            && !string.IsNullOrWhiteSpace(settings.AiModel);
            if (aiUsable)
            {
                var ai = await TryDraftWithAiAsync(settings!, check, safeUrl, since, minutes, symptom, cancellationToken);
                if (ai is not null) return ai;
            }
            return Templated(check, safeUrl, since, minutes, symptom);
        }

        private async Task<IncidentDraft?> TryDraftWithAiAsync(
            SiteSettings settings, StatusCheck check, string safeUrl, DateTime since, int minutes, string symptom, CancellationToken ct)
        {
            string template = string.IsNullOrWhiteSpace(settings.AiPromptTemplate)
                ? SiteSettingsService.DefaultAiPrompt : settings.AiPromptTemplate;
            string prompt = template
                .Replace("{service}", check.Title)
                .Replace("{url}", safeUrl)
                .Replace("{downSince}", since.ToString("u"))
                .Replace("{minutes}", minutes.ToString())
                .Replace("{symptom}", symptom);

            var body = new
            {
                model = settings.AiModel,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.2,
            };
            string url = settings.AiBaseUrl.TrimEnd('/') + "/chat/completions";
            int timeout = settings.AiTimeoutSeconds <= 0 ? SiteSettingsService.DefaultAiTimeoutSeconds
                : Math.Clamp(settings.AiTimeoutSeconds, SiteSettingsService.MinAiTimeoutSeconds, SiteSettingsService.MaxAiTimeoutSeconds);

            // One retry (two attempts total). Per-request timeout via a linked CTS —
            // the named client has no fixed timeout (see StatusCheckHttpClients).
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    var client = httpClientFactory.CreateClient(StatusCheckHttpClients.AiIncident);
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                    if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
                        req.Headers.Authorization = new("Bearer", settings.AiApiKey);

                    using var resp = await client.SendAsync(req, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    var payload = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, cts.Token);
                    var draft = ParseDraft(payload, check);
                    if (draft is not null) return draft;
                    // A well-formed HTTP 200 we couldn't parse won't get better on retry.
                    logger.LogWarning("AI incident draft for check {CheckId} returned unparseable content; using templated fallback.", check.Id);
                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Scrub: log the EXCEPTION TYPE only — never its message — so a
                    // provider error string can't reach logs that might surface it.
                    logger.LogWarning("AI incident draft attempt {Attempt}/2 failed for check {CheckId} ({ExType}).",
                        attempt, check.Id, ex.GetType().Name);
                }
            }
            return null; // both attempts failed → caller uses the templated fallback
        }

        private static IncidentDraft? ParseDraft(JsonElement payload, StatusCheck check)
        {
            // OpenAI-compatible: choices[0].message.content is a JSON string we parse.
            if (!payload.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            string? content = choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
                ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content)) return null;

            string json = StripFences(content.Trim());
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string title = Clamp(root.TryGetProperty("title", out var t) ? t.GetString() : null, MaxTitle);
                string desc = Clamp(root.TryGetProperty("description", out var d) ? d.GetString() : null, MaxDescription);
                if (title.Length == 0 || desc.Length == 0) return null;
                var sev = ParseSeverity(root.TryGetProperty("severity", out var s) ? s.GetString() : null);
                return new IncidentDraft(title, desc, sev, FromAi: true);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IncidentDraft Templated(StatusCheck check, string safeUrl, DateTime since, int minutes, string symptom) => new(
            Title: Clamp($"{check.Title} is experiencing an outage", MaxTitle),
            Description: Clamp(
                $"Automated report: {check.Title} ({safeUrl}) has been down since " +
                $"{since:u} (~{minutes} min). Symptom: {symptom}. This incident will update automatically when the service recovers.",
                MaxDescription),
            Severity: IncidentSeverity.Minor,
            FromAi: false);

        /// <summary>Reduce a monitored URL to scheme/host/path — dropping userinfo,
        /// query, and fragment — so credentials or tokens carried in the URL can never
        /// reach the AI provider or the public incident copy. Falls back to a generic
        /// phrase for an unparseable URL.</summary>
        private static string SanitizeUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return "the monitored endpoint";
            var b = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty };
            return b.Uri.GetLeftPart(UriPartial.Path);
        }

        // Fixed, non-sensitive symptom phrasing per fail type — never raw error text.
        private static string SymptomPhrase(FailType failType) => failType switch
        {
            FailType.StatusCode => "returned an unexpected HTTP status code",
            FailType.ResponseTime => "responded slower than the configured threshold",
            FailType.Unreachable => "was unreachable",
            _ => "is not healthy",
        };

        private static IncidentSeverity ParseSeverity(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "critical" => IncidentSeverity.Critical,
            "severe" or "major" => IncidentSeverity.Severe,
            _ => IncidentSeverity.Minor,
        };

        private static string StripFences(string s)
        {
            if (!s.StartsWith("```")) return s;
            int firstNl = s.IndexOf('\n');
            if (firstNl < 0) return s;
            s = s[(firstNl + 1)..];
            int fence = s.LastIndexOf("```", StringComparison.Ordinal);
            return (fence >= 0 ? s[..fence] : s).Trim();
        }

        private static string Clamp(string? value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length > max ? value[..max] : value;
        }
    }
}
