namespace SuperStatus.ServiceDefaults;

/// <summary>
/// Issue #377 — the <c>PUBLIC_DEMO</c> switch for the hosted public demo instance
/// (<c>demo.status.superstatus.io</c>).
///
/// <para>When enabled, the instance seeds a well-known administrator
/// (<c>admin@superstatus.io</c> / <c>admin</c>), advertises that credential on its
/// own login page, and shows a site-wide banner counting down to the hourly wipe.
/// It is the ONLY thing standing between those affordances and a real
/// deployment, so it is deliberately: off by default, absent from every compose
/// and env file except <c>docker-compose.demo.yml</c>, and asserted-absent by
/// tests.</para>
///
/// <para><b>Not to be confused with <c>SUPERSTATUS_DEMO=1</c></b>, which is an
/// unrelated Development-only harness that swaps SuperStatus.Web's API clients for
/// in-memory fixtures so the UI can be screenshotted without a backend (see
/// <c>SuperStatus.Web/DemoData/</c>). That one fakes the data; this one is a real
/// stack with real data that happens to be disposable.</para>
/// </summary>
public static class DemoMode
{
    /// <summary>Environment variable that enables public-demo mode.</summary>
    public const string EnvironmentVariable = "PUBLIC_DEMO";

    // The seeded demo administrator's credentials live on
    // SuperStatus.Data's SuperStatusIdentityDbInitializer, next to AdministratorRole —
    // that project has no reference to this one, and the seed is what owns them.

    /// <summary>
    /// Parses the raw flag value. Only the exact string <c>"true"</c>
    /// (case-insensitive, surrounding whitespace ignored) enables demo mode; every
    /// other value — including <c>"1"</c>, <c>"yes"</c>, empty, and null — leaves it
    /// off. Strictness is the point: a typo must fail closed, and the null-map form
    /// (<c>PUBLIC_DEMO:</c>) in compose must not accidentally enable it.
    /// </summary>
    public static bool IsEnabled(string? rawValue) =>
        !string.IsNullOrWhiteSpace(rawValue)
        && rawValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <see cref="EnvironmentVariable"/> from the process environment.</summary>
    public static bool IsEnabledFromEnvironment() =>
        IsEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// The next scheduled reset — the next top of the hour, UTC. The reset itself is
    /// driven by a systemd timer with <c>OnCalendar=hourly</c> on the docker host
    /// (<c>scripts/demo-reset.sh</c>), so both sides derive the same instant from the
    /// clock and never need to share state. Exactly on the hour this returns the
    /// following hour, never <paramref name="utcNow"/> itself.
    /// </summary>
    public static DateTime NextResetUtc(DateTime utcNow)
    {
        var topOfHour = new DateTime(
            utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
        return topOfHour.AddHours(1);
    }

    /// <summary>Time remaining until <see cref="NextResetUtc"/>. Never negative.</summary>
    public static TimeSpan TimeUntilReset(DateTime utcNow)
    {
        var remaining = NextResetUtc(utcNow) - utcNow;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
