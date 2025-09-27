using Quartz;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Services;

namespace SuperStatus.Scheduler
{
    public class SuperStatusCheckJob : IJob
    {
        private readonly IStatusCheckService statusCheckService;
        private readonly ILogger<StatusCheckService> logger;

        public SuperStatusCheckJob(IStatusCheckService statusCheckService, ILogger<StatusCheckService> logger)
        {
            this.statusCheckService = statusCheckService;
            this.logger = logger;
        }
        public async Task Execute(IJobExecutionContext context)
        {
            IPagedResult<StatusCheck> statusCheckList = await statusCheckService.GetStatusCheckSet();
            logger.LogInformation($"Executing status check for {statusCheckList.Results.Count} endpoints...");
            foreach (StatusCheck statusCheck in statusCheckList.Results)
            {
                logger.LogInformation($"Executing status check for {statusCheck.Title}...");
                HistoricalStatusData statusCheckResult = await statusCheckService.ExecuteStatusCheck(statusCheck);
                logger.LogInformation($"Completed status check for {statusCheck.Title} with response time {statusCheckResult.ResponseTimeInMs} and StatusCode {statusCheckResult.HttpStatusCode}!");

                statusCheckResult = await statusCheckService.SaveStatusCheckResult(statusCheckResult);
                if(statusCheckResult == null)
                {
                    return;
                }
                logger.LogInformation($"Saved status check result for {statusCheck.Title}!");
                await statusCheckService.RunPostStatusCheckTasks(statusCheck, statusCheckResult);

            }
        }
    }
}
