using Quartz;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services;
using SuperStatus.Services.Services;

namespace SuperStatus.Scheduler
{
    public class SuperStatusCleanUpJob(
        IHistoricalStatusDataRepository historicalStatusDataRepository, 
        IConfigurationRepository configurationRepository,
        ILogger<StatusCheckService> logger) : IJob
    {
        private readonly IHistoricalStatusDataRepository historicalStatusDataRepository = historicalStatusDataRepository;
        private readonly IConfigurationRepository configurationRepository = configurationRepository;
        private readonly ILogger<StatusCheckService> logger = logger;

        public async Task Execute(IJobExecutionContext context)
        {
            logger.LogInformation("Cleaning up old historical data...");
            
            var config = await configurationRepository.GetConfigurationAsync();
            
            List<HistoricalStatusData> historicalData = await historicalStatusDataRepository.GetHistoricalStatusDataOlderThanXDays(config.StatusCheckGraphViewMaxDays);
            await historicalStatusDataRepository.DeleteManyAndSave(historicalData, new CancellationToken());
            
            logger.LogInformation($"Cleaned up {historicalData.Count} historical data records older than {config.StatusCheckGraphViewMaxDays} days.");
        }
    }
}
