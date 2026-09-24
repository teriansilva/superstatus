using SuperTalk.Common.Constants;

namespace SuperStatus.Data.Utilities;

/// <summary>
/// Utility functionality for environment related entities or strings.
/// </summary>
public static class EnvironmentUtilities
{
    private static readonly List<string> QaEnvironmentNames =
    [
        EnvironmentName.Development,
        EnvironmentName.Staging
    ];

    /// <summary>
    /// Checks if the given environment name is a QA environment.
    /// </summary>
    /// <param name="environmentName">Name of environment.</param>
    /// <returns>True if the environment name corresponds to a QA environment, otherwise false.</returns>
    public static bool IsQaEnvironment(string? environmentName)
    {
        return environmentName != null && QaEnvironmentNames.Contains(environmentName);
    }

    /// <summary>
    /// Checks if the given environment name is a QA environment.
    /// </summary>
    /// <param name="environmentName">Name of environment.</param>
    /// <returns>True if the environment name corresponds to a QA environment, otherwise false.</returns>
    public static bool IsDevOrQaEnvironment(string? environmentName)
    {
        return environmentName != null &&
               (environmentName == EnvironmentName.Development || IsQaEnvironment(environmentName));
    }

    /// <summary>
    /// Checks if the given environment name is a local dev environment.
    /// </summary>
    /// <param name="environmentName">Name of environment.</param>
    /// <returns>True if the environment is a local dev environment, otherwise false.</returns>
    public static bool IsDevEnvironment(string? environmentName)
    {
        return environmentName is EnvironmentName.Development;
    }

    /// <summary>
    /// Gets current environment name from environment variable.
    /// </summary>
    /// <returns>Name of current environment</returns>
    public static string? GetCurrentEnvironmentName()
    {
        return Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    }

    /// <summary>
    /// Gets current machine name.
    /// </summary>
    /// <returns>Name of current machine</returns>
    public static string GetCurrentMachineName()
    {
        return Environment.MachineName;
    }
}