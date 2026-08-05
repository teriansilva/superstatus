namespace SuperStatus.Data.Constants;

/// <summary>
/// Operator-assigned severity of an <see cref="Entities.Incident"/>
/// (issue #106). Maps to the tactical-HUD incident styling: Minor →
/// amber (.incident.minor), Severe/Critical → red (.incident.severe).
///
/// Persisted as the underlying int — values are stable; appending new
/// levels is safe, reordering is not.
/// </summary>
public enum IncidentSeverity
{
    Minor = 0,
    Severe = 1,
    Critical = 2,
}
