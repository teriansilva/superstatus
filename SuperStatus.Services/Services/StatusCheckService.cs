using Microsoft.Extensions.Logging;
using SuperStatus.Data.Constants;
using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Data.Extensions;
using System.Diagnostics;

namespace SuperStatus.Services.Services
{
    public interface IStatusCheckService
    {
        Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0, bool onlyEnabled = true);
        Task<StatusCheck?> GetStatusCheck(long StatusCheckId);
        Task<IPagedResult<StatusCheckViewModel>> GetStatusCheckViewModelSet(int page = 1, int pageSize = 0, bool onlyEnabled = true);
        Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId);
        Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0);
        Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays);
        Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays);
        Task<HistoricalStatusData> ExecuteStatusCheck(StatusCheck statusCheck);
        Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData statusCheckResult);
        Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData statusCheckResult);
        Task<StatusCheck> AddOrUpdateStatusCheck(StatusCheckViewModelBase statusCheck);
    }
    public class StatusCheckService(IStatusCheckRepository statusCheckRepository, IHistoricalStatusDataRepository historicalStatusDataRepository, IHistoricalStatusActionRepository historicalStatusActionRepository, ILogger<StatusCheckService> logger) : IStatusCheckService
    {
        public async Task<IPagedResult<StatusCheck>> GetStatusCheckSet(int page = 1, int pageSize = 0, bool onlyEnabled = true)
        {
            return await statusCheckRepository.GetStatusCheckSet(page, pageSize, onlyEnabled);
        }
        public async Task<StatusCheck?> GetStatusCheck(long StatusCheckId)
        {
            return await statusCheckRepository.GetStatusCheckById(StatusCheckId);
        }
        public async Task<IPagedResult<StatusCheckViewModel>> GetStatusCheckViewModelSet(int page = 1, int pageSize = 0, bool onlyEnabled = true)
        {
            IPagedResult<StatusCheck> statusCheckSet = await GetStatusCheckSet(page, pageSize, onlyEnabled);
            return await statusCheckSet.MapToAsync(async x => new StatusCheckViewModel(x, await GetMostRecentHistoricalStatusData(x.Id)));
        }
        public async Task<HistoricalStatusDataViewModel?> GetMostRecentHistoricalStatusData(long statusCheckId)
        {
            HistoricalStatusData? mostRecentHistoricalStatusData = await historicalStatusDataRepository.GetMostRecentHistoricalStatusData(statusCheckId);
            StatusCheck? statusCheck = await GetStatusCheck(statusCheckId);
            return mostRecentHistoricalStatusData != null ? new HistoricalStatusDataViewModel(mostRecentHistoricalStatusData) : null;
        }
        public async Task<IPagedResult<HistoricalStatusData>> GetPagedHistoricalStatusDataForStatusCheckId(long statusCheckId, int page, int pageSize = 0)
        {
            return await historicalStatusDataRepository.GetHistoricalStatusDataSetForStatusCheckId(statusCheckId, page, pageSize);
        }
        public async Task<IDictionary<DateTime, List<HistoricalStatusData>>> GetHistoricalStatusDataForStatusCheckIdByDays(long statusCheckId, int timeRangeInDays)
        {
            return await historicalStatusDataRepository.GetHistoricalStatusDataSetForDaysGroupedByDays(statusCheckId, timeRangeInDays);
        }
        public async Task<List<HistoricalStatusDataOverviewChartViewModel>> GetHistoricalStatusDataOverviewForRecentTimeRange(long statusCheckId, int timeRangeInDays)
        {
            DateTime referenceTime = DateTime.UtcNow;
            DateOnly currentDate = DateOnly.FromDateTime(referenceTime);
            StatusCheck? statusCheck = await GetStatusCheck(statusCheckId);

            if (statusCheck == null)
            {
                logger.LogInformation($"Failed to find status check with id {statusCheckId}");
                throw new Exception($"Failed to find status check with id {statusCheckId}");
            }

            var dbResults = await historicalStatusDataRepository.GetHistoricalStatusDataFailuresOverviewSetForDaysGroupedByDays(statusCheck, timeRangeInDays);
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
            StatusCheckResult result = new StatusCheckResult(statusCheck, responseTimeInMs, httpStatusCode, checkFailed);
            HistoricalStatusData historicalStatusData = new HistoricalStatusData(result, CalculateFailTypeOfHistoricalStatusData(statusCheck, result));
            historicalStatusData.HistoricalStatusAction = await RunPostStatusCheckTasks(statusCheck, historicalStatusData);
            return historicalStatusData;
        }
        public async Task<HistoricalStatusData> SaveStatusCheckResult(HistoricalStatusData historicalStatusData)
        {
            return await historicalStatusDataRepository.AddAndSave(historicalStatusData);
        }
        public async Task<HistoricalStatusAction?> RunPostStatusCheckTasks(StatusCheck statusCheck, HistoricalStatusData historicalStatusData)
        {
            if (!statusCheck.IsWebHookOnErrorEnabled
                || string.IsNullOrWhiteSpace(statusCheck.WebHookOnErrorUrl))
            {
                return null;
            }

            if (historicalStatusData.FailType == FailType.NoFail)
            {
                return null;
            }

            if (await IsWebhookThrottleInEffect(statusCheck))
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

            return new HistoricalStatusAction(historicalStatusData, ActionType.Webhook, DateTime.UtcNow);
        }
        public async Task<StatusCheck> AddOrUpdateStatusCheck(StatusCheckViewModelBase statusCheck)
        {
            if (statusCheck.Id > 0)
            {
                var existingStatusCheck = await statusCheckRepository.GetStatusCheckById(statusCheck.Id) ?? throw new Exception($"Failed to find status check with id {statusCheck.Id}");

                existingStatusCheck.Title = statusCheck.Title;
                existingStatusCheck.StatusCheckUrl = statusCheck.StatusCheckUrl;
                existingStatusCheck.IsWebHookOnErrorEnabled = statusCheck.IsWebHookOnErrorEnabled;
                existingStatusCheck.WebHookOnErrorUrl = statusCheck.WebHookOnErrorUrl;
                existingStatusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes = statusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes;
                existingStatusCheck.ExpectedStatusCode = statusCheck.ExpectedStatusCode;
                existingStatusCheck.ExpectedResponseTimeInMs = statusCheck.ExpectedResponseTimeInMs;
                existingStatusCheck.Description = statusCheck.Description;
                existingStatusCheck.Enabled = statusCheck.Enabled;
                existingStatusCheck.ServiceLogoUrl = statusCheck.ServiceLogoUrl;

                return await statusCheckRepository.UpdateAndSave(existingStatusCheck);

            }

            var newStatusCheck = new StatusCheck
            {
                Title = statusCheck.Title,
                StatusCheckUrl = statusCheck.StatusCheckUrl,
                IsWebHookOnErrorEnabled = statusCheck.IsWebHookOnErrorEnabled,
                WebHookOnErrorUrl = statusCheck.WebHookOnErrorUrl,
                ThrottleWebHookToExecuteOnlyEveryXMinutes = statusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes,
                ExpectedStatusCode = statusCheck.ExpectedStatusCode,
                ExpectedResponseTimeInMs = statusCheck.ExpectedResponseTimeInMs,
                Description = statusCheck.Description,
                Enabled = statusCheck.Enabled,
                ServiceLogoUrl = statusCheck.ServiceLogoUrl
            };
            return await statusCheckRepository.AddAndSave(newStatusCheck);

        }
        private FailType CalculateFailTypeOfHistoricalStatusData(StatusCheck statusCheck, StatusCheckResult statusCheckResult)
        {
            if (statusCheckResult.CheckFailed)
            {
                return FailType.Unreachable;
            }
            else if (statusCheckResult.HttpStatusCode != statusCheck.ExpectedStatusCode)
            {
                return FailType.StatusCode;
            }
            else if (statusCheckResult.ResponseTimeInMs > statusCheck.ExpectedResponseTimeInMs)
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
            HistoricalStatusAction? statusAction = await historicalStatusActionRepository.GetMostRecentHistoricalStatusAction(statusCheck.Id);
            if (statusAction == null)
            {
                return false;
            }
            if (statusAction.TimeOfExecutionUTC.AddMinutes(statusCheck.ThrottleWebHookToExecuteOnlyEveryXMinutes) > DateTime.UtcNow)
            {
                return true;
            }
            return false;
        }

    }
}
