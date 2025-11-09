using Microsoft.AspNetCore.Mvc;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;

namespace SuperStatus.ApiService.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/statuscheck", async (IStatusCheckService statusCheckService) =>
        {
            IPagedResult<StatusCheckViewModel> statusCheck = await statusCheckService.GetStatusCheckViewModelSet();
            if (statusCheck.RowCount == 0)
            {
                return Results.NotFound("No status checks found.");
            }
            return Results.Ok(statusCheck);
        });

        app.MapGet("/statuscheck/gethistoricaldata/{id}", async (int id, IStatusCheckService statusCheckService, IConfigurationRepository configurationRepository) =>
        {
            var config = await configurationRepository.GetConfigurationAsync();
            return await statusCheckService.GetHistoricalStatusDataOverviewForRecentTimeRange(id, config.StatusCheckGraphViewMaxDays);
        });

        app.MapPost("/statuscheck/edit", async (StatusCheckViewModelBase statusCheckToUpdate, IStatusCheckService statusCheckService) =>
        {
            await statusCheckService.AddOrUpdateStatusCheck(statusCheckToUpdate);
            return Results.Ok();
        }).RequireAuthorization();


        app.MapGet("/incidents", async (IIncidentService incidentService) =>
        {
            IDictionary<DateTime, List<IncidentViewModel>> incidents = await incidentService.GetIncidentViewModelSetForDays();
            if (incidents.Count == 0)
            {
                return Results.NotFound("No incidents found.");
            }
            return Results.Ok(incidents);
        });
    }
}