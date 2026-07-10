using System.Net;
using System.Text;
using System.Text.Json;

namespace SuperStatus.Web.DemoData;

/// <summary>
/// Issue #126/#139: Development-only message handler that answers the API
/// client GET endpoints from <see cref="DemoData"/> instead of the real API —
/// so the visual harness can render the actual pages with a realistic fleet and
/// no backend. Registered ONLY when <c>SUPERSTATUS_DEMO=1</c> in Development.
/// Mutating endpoints (edit/run-now) return 200 no-op. Never used in prod.
/// </summary>
public sealed class DemoApiHandler : HttpMessageHandler
{
    // Web defaults so JsonPropertyName attributes (snake_case summary) round-trip
    // exactly as the real API serializes them.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;

        // Issue #334: the Updates panel is stateful — the auto-update policy must
        // survive a reload for the browser harness to prove it round-trips. Handled
        // before the read-only table below because POST /auto mutates.
        if (path is "/api/updates" or "/api/updates/auto" or "/api/updates/apply")
            return Respond(request, await DemoUpdates.HandleAsync(request, cancellationToken));

        object? body = path switch
        {
            "/statuscheck/summary" => DemoData.Summary(),
            "/settings" => DemoData.Settings(),
            "/statuscheck" => DemoData.Checks(),
            "/statuscheck/providers" => DemoData.CheckProviders(),
            "/notifications/providers" => DemoData.NotificationProviders(),
            "/api/push/subscriptions/count" => new { count = 2 },
            "/api/push/vapid-key" => new { key = "BPdemoPublicKey" },
            "/incidents" => DemoData.Incidents(),
            _ when path.StartsWith("/statuscheck/gethistoricaldata/", StringComparison.Ordinal)
                => DemoData.Historical(ParseTrailingId(path)),
            // GET /statuscheck/{id}/recent?count=N — the service-detail page's
            // first call; an unknown id must 404 so the page shows its real
            // not-found shell rather than a bogus seeded detail.
            _ when path.StartsWith("/statuscheck/", StringComparison.Ordinal) && path.EndsWith("/recent", StringComparison.Ordinal)
                => DemoData.IsKnownId(ParseMiddleId(path)) ? DemoData.RecentTicks(ParseMiddleId(path), 8) : null,
            _ => null,
        };

        HttpResponseMessage response;
        if (body is not null)
        {
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            };
        }
        else if (request.Method != HttpMethod.Get)
        {
            // edit / run-now / pause — no-op OK for the demo render.
            response = new HttpResponseMessage(HttpStatusCode.OK);
        }
        else
        {
            response = new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        response.RequestMessage = request;
        return response;
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, HttpResponseMessage response)
    {
        response.RequestMessage = request;
        return response;
    }

    private static long ParseTrailingId(string path)
    {
        var tail = path[(path.LastIndexOf('/') + 1)..];
        return long.TryParse(tail, out var id) ? id : 1;
    }

    // /statuscheck/{id}/recent → {id}
    private static long ParseMiddleId(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[^2], out var id) ? id : 0;
    }
}
