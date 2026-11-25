using System.Text.RegularExpressions;

namespace SuperStatus.ApiService.Configuration.Routing;

/// <summary>
/// Transforms the route parameter to a restful format => camel case and hyphen separated (if multiple words).
/// </summary>
public partial class RestfulRouteTransformer : IOutboundParameterTransformer
{
    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelToHyphenRegex();

    /// <summary>
    /// Transforms the route parameter to a restful format => camel case and hyphen separated (if multiple words).
    /// </summary>
    /// <param name="value">Value to transform</param>
    /// <returns>Transformed value</returns>
    public string? TransformOutbound(object? value)
    {
        if (value is null)
            return null;

        string hyphenated = CamelToHyphenRegex()
            .Replace(value.ToString()!, "$1-$2")
            .ToLowerInvariant();

        return hyphenated;
    }
}