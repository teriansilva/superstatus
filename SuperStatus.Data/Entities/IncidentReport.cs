namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents an incident report that can be created by the system or manually by a user
    /// </summary>
    public class IncidentReport : EntityBase
    {
        public bool AuotmaticallyGeneratedReport { get; set; }
        public string Title { get; set; }
        public ICollection<HistoricalStatusData>? HistoricalStatusData { get; set; }
        public bool Resolved { get; set; }
        public string? Description { get; set; }
        public bool VisibleToPublic { get; set; }
    }
}