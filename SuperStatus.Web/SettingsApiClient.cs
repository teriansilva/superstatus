using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web;

/// <summary>
/// Issue #167 Phase 2: typed client over the site-settings API (the Phase 1
/// <c>/settings</c> endpoints). <see cref="GetSettingsAsync"/> reads the public,
/// cached singleton; <see cref="SaveSettingsAsync"/> persists an operator edit.
/// Both ride the machine ("apiservice") client-credentials token like
/// <see cref="StatusApiClient"/>, so the authorized POST is satisfied without a
/// per-user bearer. GET degrades to defaults on transport failure so the shell
/// always renders (the provider must never crash the circuit over branding).
/// </summary>
public class SettingsApiClient(HttpClient httpClient)
{
    public async Task<SiteSettingsViewModel> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SiteSettingsViewModel>("/settings", cancellationToken)
                   ?? new SiteSettingsViewModel();
        }
        catch (Exception)
        {
            // Calm fallback: no custom branding → the theme defaults apply.
            return new SiteSettingsViewModel();
        }
    }

    /// <summary>#184: like GetSettingsAsync but returns null on transport failure
    /// (instead of a default VM), so callers can tell "confirmed not onboarded"
    /// apart from "couldn't reach the API" — the latter must NOT bounce public
    /// visitors into setup on the root page.</summary>
    public async Task<SiteSettingsViewModel?> TryGetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SiteSettingsViewModel>("/settings", cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Persist an operator edit. Returns the server-validated result
    /// (accent normalized, logo scheme-checked). Throws on non-success so the
    /// panel can surface the failure.</summary>
    public async Task<SiteSettingsViewModel> SaveSettingsAsync(SiteSettingsViewModel settings, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/settings", settings, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to save site settings. {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }
        return await response.Content.ReadFromJsonAsync<SiteSettingsViewModel>(cancellationToken: cancellationToken)
               ?? settings;
    }

    /// <summary>#181: mark the first-run setup wizard complete (stamps OnboardedUtc).
    /// Returns the updated settings. Throws on non-success.</summary>
    public async Task<SiteSettingsViewModel> CompleteOnboardingAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync("/settings/onboarded", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SiteSettingsViewModel>(cancellationToken: cancellationToken)
               ?? new SiteSettingsViewModel();
    }
}
