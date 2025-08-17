using Quartz;
using SuperStatus.Configuration;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Services;

namespace SuperStatus.Scheduler
{
    public class SuperStatusCleanUpJob(IHistoricalStatusDataRepository historicalStatusDataRepository, ILogger<StatusCheckService> logger) : IJob
    {
        private readonly IHistoricalStatusDataRepository historicalStatusDataRepository = historicalStatusDataRepository;
        private readonly ILogger<StatusCheckService> logger = logger;

        public async Task Execute(IJobExecutionContext context)
        {
            
            logger.LogInformation($"Cleaning up old historical data...");
            List<HistoricalStatusData> historicalData = await historicalStatusDataRepository.GetHistoricalStatusDataOlderThanXDays(SuperStatusConfig.StatusCheckGraphViewMaxDays);
            await historicalStatusDataRepository.DeleteManyAndSave(historicalData, new CancellationToken());
            
        }
    }
}
