using Microsoft.VisualBasic;
using SuperStatus.ApiService.Configuration;
using SuperStatus.Configuration;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using System.Diagnostics;
using System.Reflection;

namespace SuperStatus.Services
{
    public interface IStatusCheckService
    {
        List<StatusCheck> LoadStatusCheckFromConfig();
        StatusCheck? LoadSpecificStatusCheckFromConfig(long statusCheckId);
        IPagedResult<StatusCheck> GetStatusCheckSet(int page, int pageSize = 0);
        Task<List<StatusCheckViewModel>> GetStatusCheckViewModelSet();
        Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId);
        Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0);
        Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays, int maxBatchSize);
        Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays);
        Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck);
        Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData statusCheckResult);
        Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData statusCheckResult);
    }
    public class StatusCheckService : IStatusCheckService
    {
        private readonly ISuperStatusRepository repository;
        private readonly ILogger<StatusCheckService> logger;
        public StatusCheckService(ISuperStatusRepository repository, ILogger<StatusCheckService> logger)
        {
            this.repository = repository;
            this.logger = logger;
        }

        /// <summary>
        /// Load status checks from configuration
        /// </summary>
        /// <returns><see cref="List<StatusCheck>"/></returns>
        public List<StatusCheck> LoadStatusCheckFromConfig()
        {
            return SuperStatusConfig.LoadStatusChecksFromConfiguration();
        }

        public StatusCheck LoadSpecificStatusCheckFromConfig(long statusCheckId)
        {
            StatusCheck? statusCheck = SuperStatusConfig.LoadStatusChecksFromConfiguration().FirstOrDefault(x => x.Id == statusCheckId);
            if(statusCheck == null)
            {
                logger.LogInformation($"Failed to find status check with id {statusCheckId}");
                throw new Exception($"Failed to find status check with id {statusCheckId}");
            }
            return statusCheck;
        }

        public IPagedResult<StatusCheck> GetStatusCheckSet(int page, int pageSize = 0)
        {
            //TODO: Implement DB entities
            throw new NotImplementedException();
        }

        public async Task<List<StatusCheckViewModel>> GetStatusCheckViewModelSet()
        {
            List<StatusCheck> statusCheckSet = LoadStatusCheckFromConfig();
            List<StatusCheckViewModel> statusCheckViewModelSet = new List<StatusCheckViewModel>();
            foreach(var statusCheck in statusCheckSet)
            {
                var mostRecentHistoricalStatusData = await GetMostRecentHistoricalStatusData(statusCheck.Id);
                statusCheckViewModelSet.Add(new StatusCheckViewModel(statusCheck, mostRecentHistoricalStatusData));
            }
            return statusCheckViewModelSet;
        }

        public async Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId)
        {
            HistoricalStatusData? mostRecentHistoricalStatusData = await repository.GetMostRecentHistoricalStatusData(statusCheckId);
            StatusCheck statusCheck = LoadSpecificStatusCheckFromConfig(statusCheckId);
            return mostRecentHistoricalStatusData != null ? new HistoricalStatusDataViewModel(mostRecentHistoricalStatusData, CalculateFailTypeOfHistoricalStatusData(statusCheck, mostRecentHistoricalStatusData)) : null;
        }

        public async Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0)
        {
            return await repository.GetHistoricalStatusDataSetForStatusCheckId(statusCheckId, page, pageSize);
        }
        public async Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays, int maxBatchSize)
        {
            return await repository.GetHistoricalStatusDataSetForDaysGroupedByDays(statusCheckId, timeRangeInDays, maxBatchSize);
        }
        public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays)
        {
            DateTime referenceTime = DateTime.UtcNow;
            DateOnly currentDate = DateOnly.FromDateTime(referenceTime);
            StatusCheck statusCheck = LoadSpecificStatusCheckFromConfig(statusCheckId);

            if (statusCheck == null)
            {
                logger.LogInformation($"Failed to find status check with id {statusCheckId}");
                throw new Exception($"Failed to find status check with id {statusCheckId}");
            }

            var dbResults = await repository.GetHistoricalStatusDataFailuresOverviewSetForDaysGroupedByDays(statusCheck, timeRangeInDays);
            var historicalStatusDataOverviewSet = new List<HistoricalStatusDataOverviewChartViewModel>();

            for (int i = 0; i < timeRangeInDays; i++)
            {
                DateOnly date = currentDate.AddDays(-i);
                var startDateTime = date.ToDateTime(TimeOnly.MinValue); // Start of the day
                var endDateTime = date.ToDateTime(TimeOnly.MaxValue);   // End of the day
                var filteredResults = dbResults.Where(x => x.StatusCheckId == statusCheck.Id
                                           && x.TimeOfCheckUTC >= startDateTime
                                           && x.TimeOfCheckUTC <= endDateTime).ToList();

                int failed = filteredResults.Count(x => x.HttpStatusCode != statusCheck.ExpectedStatusCode);
                int slow = filteredResults.Count(x => x.ResponseTimeInMs > statusCheck.ExpectedResponseTimeInMs);
                int unreachable = filteredResults.Count(x => x.CheckFailed);

                historicalStatusDataOverviewSet.Add(new HistoricalStatusDataOverviewChartViewModel(statusCheck.Id, date, failed, slow, unreachable));
            }

            return historicalStatusDataOverviewSet.OrderBy(x => x.Date).ToList(); 
        }

        public async Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck)
        {

            var stopwatch = new Stopwatch();
            var client = new HttpClient();
            bool checkFailed = false;

            int httpStatusCode;
            long responseTimeInMs;
            try
            {
                stopwatch.Start();
                var response = await client.GetAsync(statusCheck.StatusCheckUrl);
                stopwatch.Stop();

                httpStatusCode = (int)response.StatusCode;
                responseTimeInMs = stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                logger.LogInformation($"Failed to execute status check on {statusCheck.StatusCheckUrl}: {ex.Message}");
                httpStatusCode = 0;
                responseTimeInMs = 0;
                checkFailed = true;
            }

            HistoricalStatusData historicalStatusData = new HistoricalStatusData(new StatusCheckResult(statusCheck, responseTimeInMs, httpStatusCode, checkFailed));
            historicalStatusData.HistoricalStatusAction = await RunPostStatusCheckTasks(statusCheck, historicalStatusData);
            return historicalStatusData;
        }

        public async Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData statusCheckResult)
        {
            await repository.AddAsync(statusCheckResult);
            if (await repository.SaveAllAsync())
            {
                return statusCheckResult;
            }

            logger.LogInformation("Failed to save status check result");
            throw new Exception("Failed to save status check result");
        }

        public async Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData statusCheckResult)
        {
            if (!statusCheck.IsWebHookOnErrorEnabled
                || string.IsNullOrWhiteSpace(statusCheck.WebHookOnErrorUrl))
            {
                return null;
            }

            if (CalculateFailTypeOfHistoricalStatusData(statusCheck, statusCheckResult) == FailType.NoFail)
            {
                return null;
            }

            if(await IsWebhookThrottleInEffect(statusCheck))
            {
                logger.LogInformation($"NOT running task for {statusCheck.Title} because of active throttle!");
                return null;
            }

            logger.LogInformation($"Executing status check post tasks for {statusCheck.Title}...");
            var client = new HttpClient();
            var response = await client.GetAsync(statusCheck.WebHookOnErrorUrl);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation($"Failed to execute web hook on error for {statusCheck.Title}");
                return null;
            }

            return new HistoricalStatusAction(statusCheckResult, ActionType.Webhook, DateTime.UtcNow);
        }

        private FailType CalculateFailTypeOfHistoricalStatusData(StatusCheck statusCheck, HistoricalStatusData statusData)
        {
            if (statusData.CheckFailed)
            {
                return FailType.Unreachable;
            }
            else if (statusData.HttpStatusCode != statusCheck.ExpectedStatusCode)
            {
                return FailType.StatusCode;
            }
            else if (statusData.ResponseTimeInMs > statusCheck.ExpectedResponseTimeInMs)
            {
                return FailType.ResponseTime;
            }
            else 
            {                 
                return FailType.NoFail;
            }
        }

        private async Task<bool> IsWebhookThrottleInEffect(StatusCheck statusCheck)
        {
            HistoricalStatusAction? statusAction = await repository.GetMostRecentHistoricalStatusAction(statusCheck.Id);
            if (statusAction == null)
            {
                return false;
            }
            if(statusAction.TimeOfExecutionUTC.AddMinutes(statusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes) > DateTime.UtcNow)
            {
                return true;
            }
            return false;
        }

    }
}
