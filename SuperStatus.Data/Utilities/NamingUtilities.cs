namespace SuperStatus.Data.Utilities;

/// <summary>
/// Utility class for naming operations.
/// </summary>
public static class NamingUtilities
{
    /// <summary>
    /// Validate and sanitize a name.
    /// </summary>
    /// <param name="name">Name to be validated and sanitized</param>
    /// <returns>Sanitized name</returns>
    public static string ValidateAndSanitizeName(this string name)
    {
        ValidateNameNotNullOrEmpty(name);

        return name.SanitizeName()!;
    }

    /// <summary>
    /// Sanitize a name.
    /// </summary>
    /// <param name="name">Name to be sanitized</param>
    /// <returns>Sanitized name</returns>
    public static string? SanitizeName(this string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return name.Trim();
    }

    private static void ValidateNameNotNullOrEmpty(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentNullException(nameof(name));
        }
    }
}