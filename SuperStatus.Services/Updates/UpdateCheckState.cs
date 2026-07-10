namespace SuperStatus.Services.Updates;

/// <summary>
/// Issue #249 (epic #248): the persisted update-check state read by the operator
/// console + honoured by the nightly worker. A projection of the update columns on
/// the <c>SiteSettings</c> singleton.
/// </summary>
public sealed record UpdateCheckStateDto(
    bool Enabled,
    DateTime? LastCheckedUtc,
    string? LatestVersion,
    string? LatestNotesUrl,
    string? LastCheckError);
