using System.Text.Json;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Providers;
using SuperStatus.Services.Providers.Ai;

namespace SuperStatus.ApiService;

/// <summary>
/// #433: operator-only <b>dry-run</b> probe and AI model discovery, so a check can be tried
/// before it is saved. The check-provider sibling of <see cref="NotificationChannelTestApi"/>
/// (#365) and built on the same spine — resolve provider → build the effective config (the
/// <c>ProviderConfigWriter</c> "leave blank to keep" rule) → validate against the schema →
/// execute → map the result inline. <b>Nothing is persisted</b>: a dry run never touches
/// history, rollups, backoff or alerts.
///
/// <para>Three things here are load-bearing and easy to get wrong:</para>
/// <list type="number">
/// <item><b>The stored config is only consulted for a matching provider type.</b> <c>checkId</c>
/// and <c>{type}</c> arrive as independent inputs, so without that check a provider could be
/// handed another provider's stored <c>apiKey</c> simply because both schemas use that key
/// name. The notification precedent keys on profile id <i>and</i> provider type for the same
/// reason.</item>
/// <item><b>Provider messages are scrubbed.</b> <c>ProbeResult.Message</c> carries exception
/// text (<c>HttpCheckProvider</c> returns <c>ex.Message</c>), and .NET transport exceptions
/// embed the full request URL — userinfo and query string included. Only
/// <c>ProbeResult.PublicMessage</c>, which a provider sets deliberately for text it synthesised,
/// is forwarded; otherwise the message is derived here from the outcome and status class.</item>
/// <item><b>Containment matches the engine's.</b> A timeout <i>plus</i> <c>WaitAsync</c>, so a
/// provider that ignores its cancellation token is abandoned rather than left to pin the
/// request open — a bare try/catch would not do that.</item>
/// </list>
/// </summary>
public static class CheckProviderTestApi
{
    public static void MapCheckProviderTestApi(this IEndpointRouteBuilder app)
    {
        // Operator-only. A dry run performs a real outbound request using the check's
        // credential — the same trust level as saving it, and the same targets the operator
        // can already reach via /statuscheck/edit + /statuscheck/{id}/run-now.
        app.MapPost("/statuscheck/providers/{type}/test",
            async (string type, CheckTestRequest? body, ICheckProviderRegistry registry,
                   IRepository<StatusCheck> checks, CancellationToken ct) =>
            {
                var outcome = await RunAsync(type, body ?? new CheckTestRequest(), registry, checks, ct);
                return outcome.Status switch
                {
                    CheckTestStatus.UnknownProvider => Results.NotFound(new { message = $"No check provider '{type}' is registered." }),
                    CheckTestStatus.NotTestable => Results.UnprocessableEntity(new { message = outcome.Message }),
                    CheckTestStatus.InvalidConfig => Results.UnprocessableEntity(new { message = outcome.Message }),
                    _ => Results.Ok(outcome.Body),
                };
            }).RequireAuthorization();

        // AI-specific: ask the endpoint which models it serves. Same auth and the same
        // matching-provider-type rule for the stored key.
        app.MapPost("/statuscheck/providers/ai/models",
            async (AiModelsRequest? body, IAiModelDiscovery discovery, ICheckProviderRegistry registry,
                   IRepository<StatusCheck> checks, CancellationToken ct) =>
            {
                var request = body ?? new AiModelsRequest();
                if (registry.Find(AiCheckProvider.TypeId) is null)
                    return Results.NotFound(new { message = "The AI/LLM provider is not registered." });

                string? apiKey = request.ApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    // Blank ⇒ reuse the stored key, but only from an AI check that still points
                    // at the base URL being asked about. Discovery sends the key straight to
                    // whatever host the caller names, so binding it to the stored endpoint is
                    // what stops this being a credential-export path (see
                    // ReusableStoredConfigAsync).
                    var ai = registry.Find(AiCheckProvider.TypeId)!.Descriptor;
                    var submitted = new Dictionary<string, string> { [AiCheckConfig.BaseUrlKey] = request.BaseUrl ?? string.Empty };
                    string? storedJson = await ReusableStoredConfigAsync(request.CheckId, ai, submitted, checks, ct);
                    apiKey = ReadStringField(storedJson, AiCheckConfig.ApiKeyKey);
                }

                var result = await discovery.ListModelsAsync(request.BaseUrl, apiKey, ct);
                return Results.Ok(new AiModelsResultViewModel
                {
                    Ok = result.Ok,
                    SupportsListing = result.SupportsListing,
                    Models = result.Models.ToList(),
                    Message = result.Message,
                });
            }).RequireAuthorization();
    }

    public enum CheckTestStatus { Ok, UnknownProvider, NotTestable, InvalidConfig }

    public sealed record CheckTestOutcome(CheckTestStatus Status, string? Message, CheckTestResultViewModel? Body);

    /// <summary>
    /// The HTTP-free core, so resolve → effective-config → validate → probe is unit-testable
    /// against an in-memory registry and repo (mirrors <c>NotificationChannelTestApi.RunAsync</c>).
    /// </summary>
    public static async Task<CheckTestOutcome> RunAsync(
        string type, CheckTestRequest body, ICheckProviderRegistry registry,
        IRepository<StatusCheck> checks, CancellationToken ct)
    {
        var provider = registry.Find(type);
        if (provider is null)
            return new(CheckTestStatus.UnknownProvider, null, null);

        var descriptor = provider.Descriptor;

        // A push provider has no target to reach out to — the signal comes to us. Testing it
        // would mean fabricating an inbound ping, which proves nothing about the operator's
        // agent actually being wired up.
        if (descriptor.Direction != ProbeDirection.Pull)
            return new(CheckTestStatus.NotTestable, $"The '{descriptor.DisplayName}' provider is a push check and cannot be tested from here.", null);

        var schema = descriptor.ConfigSchema;

        // "Leave blank to keep" — but only when the stored row is the same provider AND still
        // points at the same endpoint (see ReusableStoredConfigAsync). Anything else means the
        // typed values stand alone with no stored secret merged in, so a retargeted dry run
        // cannot carry the old endpoint's credential to a new host.
        var incoming = body.Config ?? new Dictionary<string, string>();
        string? storedJson = await ReusableStoredConfigAsync(body.CheckId, descriptor, incoming, checks, ct);
        var effectiveJson = ProviderConfigWriter.Build(schema, incoming, storedJson);

        var reason = schema.Validate(effectiveJson);
        if (reason is not null)
            return new(CheckTestStatus.InvalidConfig, $"{descriptor.DisplayName}: {reason}.", null);

        var probe = await RunProbeSafelyAsync(provider, body.CheckId, effectiveJson, ct);

        return new(CheckTestStatus.Ok, null, new CheckTestResultViewModel
        {
            Outcome = probe.Outcome.ToString().ToLowerInvariant(),
            Ok = probe.Outcome == ProbeOutcome.Up,
            LatencyMs = probe.LatencyMs,
            HttpStatusCode = probe.HttpStatusCode,
            Message = PublicMessage(probe),
            Metrics = MapMetrics(descriptor, probe.MetricsJson),
        });
    }

    /// <summary>
    /// The stored <c>ConfigJson</c> whose secrets may be reused for THIS request — or null.
    ///
    /// <para>Three conditions, all required. The check must exist; it must be stored against the
    /// same provider type (otherwise one provider consumes another's credential); and — the one
    /// that matters most — <b>the target the caller submitted must still be the target the
    /// secret was saved for</b>.</para>
    ///
    /// <para>Without that last check these endpoints are a credential-export primitive. A stored
    /// API key is write-only: it can never be read back through the API. But a caller who names
    /// an existing check's id, leaves the key blank, and points the base URL at a host they
    /// control would have the server send that key to that host as <c>Authorization: Bearer …</c>
    /// — one request, nothing persisted, nothing audited. The same mistake happens by accident in
    /// the normal edit flow: change an endpoint, leave the masked key blank because the UI says
    /// blank keeps it, press Validate, and the old endpoint's credential is disclosed to the new
    /// one before Save is ever pressed.</para>
    ///
    /// <para>"Same target" is compared as scheme + host + port on the provider's target field,
    /// so a path or query edit is tolerated but a different origin is not. If the provider does
    /// not declare which field carries its target, this <b>fails closed</b> and refuses to reuse
    /// the secret — a provider we cannot reason about is not one to hand a credential for.</para>
    /// </summary>
    private static async Task<string?> ReusableStoredConfigAsync(
        long checkId, ProviderDescriptor descriptor, IReadOnlyDictionary<string, string> incoming,
        IRepository<StatusCheck> checks, CancellationToken ct)
    {
        if (checkId <= 0) return null;
        var stored = await checks.FirstOrDefault(c => c.Id == checkId, ct);
        if (stored is null) return null;
        if (!string.Equals(stored.ProviderType, descriptor.TypeId, StringComparison.Ordinal)) return null;

        // Fail closed: no declared target field ⇒ we cannot prove the endpoint is unchanged.
        if (string.IsNullOrEmpty(descriptor.BatchTargetField)) return null;

        incoming.TryGetValue(descriptor.BatchTargetField, out var submittedTarget);
        string? storedTarget = ReadStringField(stored.ConfigJson, descriptor.BatchTargetField);

        return SameEndpoint(storedTarget, submittedTarget) ? stored.ConfigJson : null;
    }

    /// <summary>Do two target URLs address the same endpoint? Compared on scheme, host and port
    /// only — an operator refining a path or query keeps their stored key, while any change of
    /// origin is treated as a new destination that must be authenticated explicitly. A value
    /// that is not an absolute http(s) URL never matches, so it cannot be used to slip past
    /// this check.</summary>
    public static bool SameEndpoint(string? storedTarget, string? submittedTarget)
    {
        if (string.IsNullOrWhiteSpace(storedTarget) || string.IsNullOrWhiteSpace(submittedTarget)) return false;
        if (!Uri.TryCreate(storedTarget.Trim(), UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(submittedTarget.Trim(), UriKind.Absolute, out var b)) return false;
        if (a.Scheme != Uri.UriSchemeHttp && a.Scheme != Uri.UriSchemeHttps) return false;
        if (b.Scheme != Uri.UriSchemeHttp && b.Scheme != Uri.UriSchemeHttps) return false;

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;
    }

    /// <summary>
    /// The engine's containment, reproduced: the provider's own <c>ProbeTimeout</c> plus a
    /// <c>WaitAsync</c> backstop. <c>WaitAsync</c> is the part that matters — it abandons the
    /// await when the backstop fires, so a provider that never observes the token cannot hold
    /// the request open. A throw, a hang, or an ignored cancellation all normalize to an
    /// unreachable result.
    /// </summary>
    private static async Task<ProbeResult> RunProbeSafelyAsync(ICheckProvider provider, long checkId, string configJson, CancellationToken ct)
    {
        var timeout = provider.Descriptor.ProbeTimeout;
        var context = new ProbeContext(checkId, "Connection test", configJson, timeout);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + TimeSpan.FromSeconds(5));
        try
        {
            return await provider.ProbeAsync(context, cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // the caller went away
        }
        catch (Exception)
        {
            // Deliberately NOT surfacing ex.Message — see the class remarks.
            return ProbeResult.Unreachable();
        }
    }

    /// <summary>
    /// The scrub. Prefer what the provider explicitly published as safe; otherwise synthesise
    /// from the outcome and the status class. A provider's <c>Message</c> is never consulted.
    /// </summary>
    private static string? PublicMessage(ProbeResult probe)
    {
        if (!string.IsNullOrWhiteSpace(probe.PublicMessage)) return probe.PublicMessage;

        return probe.FailType switch
        {
            FailType.NoFail => null,
            FailType.ResponseTime => "The endpoint responded, but slower than its threshold.",
            FailType.StatusCode when probe.HttpStatusCode > 0 => $"The endpoint returned HTTP {probe.HttpStatusCode}, which is not the expected status.",
            FailType.StatusCode => "The endpoint responded, but not as expected.",
            _ when probe.HttpStatusCode > 0 => $"The endpoint returned HTTP {probe.HttpStatusCode}.",
            _ => "Could not reach the endpoint.",
        };
    }

    /// <summary>Join the probe's emitted metrics against the provider's declared
    /// <c>MetricDefs</c>, so the UI gets labels and units instead of raw keys. An undeclared key
    /// is dropped — a provider may only report what it declared.</summary>
    private static List<CheckTestMetricViewModel> MapMetrics(ProviderDescriptor descriptor, string? metricsJson)
    {
        var mapped = new List<CheckTestMetricViewModel>();
        if (string.IsNullOrWhiteSpace(metricsJson) || descriptor.MetricDefs.Count == 0) return mapped;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(metricsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return mapped;
        }
        if (root.ValueKind != JsonValueKind.Object) return mapped;

        foreach (var def in descriptor.MetricDefs)
        {
            if (!root.TryGetProperty(def.Key, out var value)) continue;
            double? number = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var d) => d,
                JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) => s,
                _ => null,
            };
            if (number is null) continue;

            mapped.Add(new CheckTestMetricViewModel
            {
                Key = def.Key,
                Label = def.Label,
                Unit = def.Unit,
                Value = number.Value,
            });
        }
        return mapped;
    }

    /// <summary>Read one string field out of a stored ConfigJson blob. Used only to recover a
    /// stored API key server-side; the value never leaves the server.</summary>
    private static string? ReadStringField(string? configJson, string key)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(key, out var v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
