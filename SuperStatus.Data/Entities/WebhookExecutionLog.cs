using System.ComponentModel.DataAnnotations.Schema;
using SuperStatus.Data.Constants;

namespace SuperStatus.Data.Entities;

/// <summary>
/// One row per outbound webhook attempt (issue #107). Audit-grade
/// truth of what hit the wire — separate from
/// <see cref="HistoricalStatusAction"/> which only records that a
/// webhook *was* invoked.
///
/// The same 30-day retention window as HistoricalStatusData applies;
/// rows are bulk-deleted by SuperStatusCleanUpJob.
/// </summary>
public class WebhookExecutionLog : EntityBase
{
    /// <summary>Owning status check. Cascades on delete.</summary>
    public long StatusCheckId { get; set; }

    [ForeignKey(nameof(StatusCheckId))]
    public virtual StatusCheck? StatusCheck { get; set; }

    /// <summary>
    /// Optional parent tick row. Nullable + ON DELETE SET NULL so retention
    /// of the parent HistoricalStatusData row never orphans the log entry.
    /// </summary>
    public long? HistoricalStatusDataId { get; set; }

    [ForeignKey(nameof(HistoricalStatusDataId))]
    public virtual HistoricalStatusData? HistoricalStatusData { get; set; }

    public DateTime AttemptedUtc { get; set; }

    /// <summary>The webhook URL at the time of the attempt; denormalized
    /// so a later edit of the StatusCheck doesn't rewrite the audit
    /// record.</summary>
    public string TargetUrl { get; set; } = string.Empty;

    /// <summary>HTTP status code received. 0 indicates no HTTP response
    /// (transport failure, timeout, or throttle skip).</summary>
    public int HttpStatusCode { get; set; }

    /// <summary>Elapsed wall-clock from request start to response or error.
    /// 0 on throttle-skipped rows.</summary>
    public int ResponseTimeMs { get; set; }

    public WebhookOutcome Outcome { get; set; }

    /// <summary>
    /// Truncated, sanitized error excerpt for failure rows. Limited to
    /// <see cref="MaxErrorMessageLength"/> characters at write time so an
    /// exception message containing a long token or response body cannot
    /// inflate the audit table.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Maximum stored characters for <see cref="ErrorMessage"/>. Anything
    /// longer is truncated with a trailing ellipsis at the service layer.
    /// </summary>
    public const int MaxErrorMessageLength = 1024;
}
