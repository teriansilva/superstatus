namespace SuperStatus.ApiService.Configuration.Settings;

/// <summary>
/// Settings for rate limiting.
/// </summary>
public class RateLimitSettings
{
    /// <summary> The maximum number of tokens allowed in the bucket i.e. maximum number of requests from same IP allowed in the time period. </summary>
    public int TokenLimit { get; set; }

    /// <summary> The number of tokens to add to the bucket in each period. </summary>
    public int TokensPerPeriod { get; set; }

    /// <summary> The time period for which the tokens are valid. </summary>
    public TimeSpan ReplenishmentPeriod { get; set; }
}