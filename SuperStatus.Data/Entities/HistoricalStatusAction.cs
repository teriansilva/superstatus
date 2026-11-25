using SuperStatus.Data.Constants;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SuperStatus.Data.Entities
{
    /// <summary>
    /// Action executed for <see cref="HistoricalStatusData"/>
    /// </summary>
    public class HistoricalStatusAction : EntityBase
    {
        public HistoricalStatusAction()
        {
        }
        public HistoricalStatusAction(HistoricalStatusData historicalStatusData, ActionType actionType, DateTime timeOfExectution)
        {
            StatusCheckId = historicalStatusData.StatusCheckId;
            HistoricalStatusData = historicalStatusData;
            ActionType = actionType;
            TimeOfExecutionUTC = timeOfExectution;
        }
        
        public long StatusCheckId { get; set; }
        public long HistoricalStatusDataId { get; set; }
        public virtual HistoricalStatusData? HistoricalStatusData { get; set; }
        public ActionType ActionType { get; set; }
        public DateTime TimeOfExecutionUTC { get; set; }
    }
}
