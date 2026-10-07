using System.Net;
using System.Text;
using System.Text.Json;
using SuperStatus.Data.ViewModels;

namespace SuperStatus.Web.DemoData;

/// <summary>
/// Issue #334: a Development-only, in-memory stand-in for the api's update endpoints,
/// so the /ui-gallery route can render the real <c>UpdatesPanel</c> — and the Playwright
/// spec can drive it — with no api / Identity / Postgres behind it.
///
/// Unlike the read-only demo endpoints, the auto-update policy is <b>stateful</b>: a
/// POST to <c>/api/updates/auto</c> updates it and a later GET reflects it, so the
/// browser test can prove the toggle and schedule actually round-trip rather than just
/// flipping a local field. State is static because the handler is constructed per
/// client. Never registered outside Development + SUPERSTATUS_DEMO=1.
/// </summary>
internal static class DemoUpdates
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    private static readonly Lock Gate = new();

    private static bool _autoEnabled;
    private static TimeOnly _autoTime = new(3, 0);
    private static bool _applying;

    public static async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path == "/api/updates/apply")
        {
            lock (Gate) { _applying = true; }
            return JsonResponse(new UpdateApplyResult { Accepted = true }, HttpStatusCode.Accepted);
        }

        if (path == "/api/updates/auto" && request.Method == HttpMethod.Post)
        {
            var raw = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            var req = JsonSerializer.Deserialize<AutoUpdateRequest>(raw, Opts) ?? new AutoUpdateRequest();

            // Mirror the api's validation exactly (same helper), so the harness exercises
            // the real failure path rather than a lenient stub.
            if (!AutoUpdateRequest.TryParseTime(req.Time, out var parsed))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            lock (Gate)
            {
                _autoEnabled = req.Enabled;
                _autoTime = parsed;
            }
        }

        return JsonResponse(Status(), HttpStatusCode.OK);
    }

    private static UpdateStatusViewModel Status()
    {
        lock (Gate)
        {
            return new UpdateStatusViewModel
            {
                CurrentVersion = "1.3.2",
                Channel = "latest",
                Status = UpdateStatusViewModel.StatusUpdateAvailable,
                CheckEnabled = true,
                LastCheckedUtc = DateTime.UtcNow.AddMinutes(-12),
                LatestVersion = "1.3.3",
                LatestNotesUrl = "https://github.com/teriansilva/superstatus/releases/tag/v1.3.3",
                LastCheckError = null,
                AutoUpdateEnabled = _autoEnabled,
                AutoUpdateTimeUtc = _autoTime,
                AutoUpdateLastRunUtc = _applying ? DateTime.UtcNow : null,
                // The gallery renders the default install: the update engine is present.
                CanApplyInApp = true,
            };
        }
    }

    private static HttpResponseMessage JsonResponse<T>(T body, HttpStatusCode code)
        => new(code) { Content = new StringContent(JsonSerializer.Serialize(body, Opts), Encoding.UTF8, "application/json") };
}
