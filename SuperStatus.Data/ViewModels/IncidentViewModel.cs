using SuperStatus.Data.Entities;

namespace SuperStatus.Data.ViewModels
{
    public class IncidentViewModel
    {
        public IncidentViewModel()
        {
            
        }
        public IncidentViewModel(Incident incident)
        {
            Id = incident.Id;
            AutomaticallyGeneratedReport = incident.AuotmaticallyGeneratedReport;
            Title = incident.Title;
            Created = incident.Created;
            HistoricalStatusData = incident.HistoricalStatusData?.Select(h => new HistoricalStatusDataViewModel(h)).ToList();
            Resolved = incident.Resolved;
            Description = incident.Description;
            VisibleToPublic = incident.VisibleToPublic;
        }
        public long Id { get; set; }
        public bool AutomaticallyGeneratedReport { get; set; }
        public string Title { get; set; } = string.Empty; 
        public IList<HistoricalStatusDataViewModel>? HistoricalStatusData { get; set; }
        public bool Resolved { get; set; }
        public string? Description { get; set; }
        public DateTime Created { get; set; }
        public bool VisibleToPublic { get; set; }
    }
}