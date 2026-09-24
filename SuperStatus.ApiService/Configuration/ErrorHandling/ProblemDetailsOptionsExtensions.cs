using Microsoft.AspNetCore.Mvc;
using SuperStatus.Data.Exceptions;
using ProblemDetailsOptions = Hellang.Middleware.ProblemDetails.ProblemDetailsOptions;

namespace SuperStatus.ApiService.Configuration.ErrorHandling;

/// <summary>
/// Extensions for customizing ProblemDetails error handling middleware.
/// </summary>
public static class ProblemDetailsOptionsExtensions
{
    /// <summary>
    /// Maps custom/certain exception types to a custom <see cref="ProblemDetails"/>.
    /// </summary>
    /// <param name="options">ProblemDetails middleware to which to add custom mapping</param>
    public static void MapExceptionsToResponses(this ProblemDetailsOptions options)
    {
        options.MapToStatusCode<NotSupportedException>(StatusCodes.Status400BadRequest);
        options.MapToStatusCode<InvalidOperationException>(StatusCodes.Status400BadRequest);
        options.MapToStatusCode<ArgumentOutOfRangeException>(StatusCodes.Status400BadRequest);
        options.MapToStatusCode<ArgumentException>(StatusCodes.Status400BadRequest);

        options.MapToStatusCode<UnauthorizedAccessException>(StatusCodes.Status403Forbidden);

        options.MapToStatusCode<ItemNotFoundException>(StatusCodes.Status404NotFound);

        options.MapToStatusCode<NotImplementedException>(StatusCodes.Status501NotImplemented);

        options.MapToStatusCode<Exception>(StatusCodes.Status500InternalServerError);
    }

    private static ProblemDetails CreateProblemDetails(
        int statusCode,
        Exception exception,
        string? type = null,
        string? detail = null)
    {
        return new ProblemDetails
        {
            Status = statusCode,
            Type = type,
            Title = exception.Message,
            Detail = detail
        };
    }
}