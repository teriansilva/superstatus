using Microsoft.AspNetCore.Mvc;
using SuperStatus.Services.Services;

namespace SuperStatus.ApiService.Endpoints;

public static class ConfigurationEndpoints
{
    public static void MapConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization();

        group.MapGet("/configuration", GetConfiguration)
            .WithName("GetConfiguration");

        group.MapPost("/configuration", UpdateConfiguration)
            .WithName("UpdateConfiguration");
    }

    private static async Task<IResult> GetConfiguration(
        [FromServices] IConfigurationService configurationService)
    {
        try
        {
            var configuration = await configurationService.GetConfigurationAsync();
            if (configuration == null)
            {
                return Results.NotFound("Configuration not found");
            }
            
            return Results.Ok(configuration);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error retrieving configuration: {ex.Message}");
        }
    }

    private static async Task<IResult> UpdateConfiguration(
        [FromBody] Data.Entities.Configuration configuration,
        [FromServices] IConfigurationService configurationService)
    {
        try
        {
            await configurationService.UpdateConfigurationAsync(configuration);
            return Results.Ok();
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating configuration: {ex.Message}");
        }
    }
}