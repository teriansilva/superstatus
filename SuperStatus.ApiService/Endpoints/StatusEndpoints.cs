using Microsoft.AspNetCore.Mvc;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Services;
using System.Security.Claims;

namespace SuperStatus.ApiService.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/statuscheck", async (ClaimsPrincipal user, IStatusCheckService statusCheckService, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("StatusEndpoints");
            var isAuthenticated = user.Identity?.IsAuthenticated ?? false;
            
            if (isAuthenticated)
            {
                // User is authenticated - you can access claims here
                var userId = user.FindFirst("sub")?.Value; // Subject claim (user ID)
                var name = user.Identity?.Name;
                logger.LogInformation("Authenticated user '{Name}' (ID: {UserId}) accessed /statuscheck", name, userId);
            }
            else  
            {
                logger.LogInformation("Anonymous user accessed /statuscheck");
            }

            IPagedResult<StatusCheckViewModel> statusCheck = await statusCheckService.GetStatusCheckViewModelSet(onlyEnabled: true);
            return Results.Ok(statusCheck);
        });

        app.MapGet("/statuscheck/admin", async (ClaimsPrincipal user, IStatusCheckService statusCheckService) =>
        {
            IPagedResult<StatusCheckViewModel> statusCheck = await statusCheckService.GetStatusCheckViewModelSet(onlyEnabled: false);
            return Results.Ok(statusCheck);
        }).RequireAuthorization();

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