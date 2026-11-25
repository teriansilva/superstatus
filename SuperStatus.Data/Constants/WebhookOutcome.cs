namespace SuperStatus.Data.Constants;

/// <summary>
/// Wire outcome of an outbound webhook attempt, captured per-attempt in
/// <see cref="Entities.WebhookExecutionLog"/> (issue #107).
///
/// Distinct values are intentional. Hermes review on #107 noted that a
/// throttle-skipped row tagged as <c>Success</c> would silently fold
/// "we fired and it worked" with "we didn't fire" in the audit filter —
/// each path therefore gets its own outcome value.
/// </summary>
public enum WebhookOutcome
{
    /// <summary>HTTP 2xx response on time.</summary>
    Success = 0,

    /// <summary>HTTP response received but non-2xx.</summary>
    NonSuccess = 1,

    /// <summary>HTTP call exceeded the configured timeout window.</summary>
    Timeout = 2,

    /// <summary>Network / DNS / TLS layer failure before any HTTP response.</summary>
    TransportFailure = 3,

    /// <summary>
    /// Throttle window prevented the attempt from firing. No wire call
    /// happened. HttpStatusCode is 0 and ResponseTimeMs is 0 on these rows.
    /// </summary>
    Skipped = 4,
}
