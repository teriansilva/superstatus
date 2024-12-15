using Quartz;
using SuperStatus.Configuration;
using SuperStatus.Data.Repositories;
using SuperStatus.Services;

namespace SuperStatus.Scheduler
{
    public class SuperStatusCleanUpJob : IJob
    {
        private readonly ISuperStatusRepository superStatusRepository;
        private readonly ILogger<StatusCheckService> logger;

        public SuperStatusCleanUpJob(ISuperStatusRepository superStatusRepository, ILogger<StatusCheckService> logger)
        {
            this.superStatusRepository = superStatusRepository;
            this.logger = logger;
        }
        public async Task Execute(IJobExecutionContext context)
        {
            
            logger.LogInformation($"Cleaning up old historical data...");
            await superStatusRepository.CleanUpHistoricalStatusDataOlderThanXDays(SuperStatusConfig.StatusCheckGraphViewMaxDays);
            
        }
    }
}
