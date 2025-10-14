namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Represents an incident report that can be created by the system or manually by a user
    /// </summary>
    public class Incident : EntityBase
    {
        public bool AuotmaticallyGeneratedReport { get; set; }
        public string Title { get; set; } = string.Empty; 
        public ICollection<HistoricalStatusData>? HistoricalStatusData { get; set; } = new List<HistoricalStatusData>();
        public bool Resolved { get; set; } 
        public string Description { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public bool VisibleToPublic { get; set; }
    }
}