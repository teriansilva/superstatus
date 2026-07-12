using SuperStatus.ServiceDefaults;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #377 — the flag parse and the reset-clock math.
///
/// <para><see cref="DemoMode.NextResetUtc"/> is the only coordination between the UI
/// countdown and the systemd timer that actually wipes the instance
/// (<c>OnCalendar=hourly</c>). There is no shared state, no API call: both sides read
/// the wall clock and agree on "the next top of the hour". These tests pin that
/// contract, especially at the boundary where an off-by-one would have the banner
/// claim a reset is 60 minutes away at the exact moment one fires.</para>
/// </summary>
[TestClass]
public class DemoModeTests
{
    [TestMethod]
    public void IsEnabled_OnlyTheExactStringTrueEnablesDemoMode()
    {
        Assert.IsTrue(DemoMode.IsEnabled("true"));
        Assert.IsTrue(DemoMode.IsEnabled("TRUE"), "compose and shells vary on case");
        Assert.IsTrue(DemoMode.IsEnabled(" true "), "trailing whitespace from an env file");
    }

    [TestMethod]
    public void IsEnabled_FailsClosedForEverythingElse()
    {
        // Fail-closed is the point: this flag seeds a publicly-known admin credential.
        // "1" and "yes" are the tempting near-misses; null/empty is the compose null-map
        // form (`PUBLIC_DEMO:`) that forwards an unset variable.
        foreach (var raw in new string?[] { null, "", "  ", "1", "yes", "on", "false", "TRUE!", "truthy" })
        {
            Assert.IsFalse(DemoMode.IsEnabled(raw), $"'{raw ?? "<null>"}' must not enable demo mode.");
        }
    }

    [TestMethod]
    public void NextResetUtc_IsTheNextTopOfTheHour()
    {
        var now = new DateTime(2026, 7, 10, 14, 17, 43, DateTimeKind.Utc);
        Assert.AreEqual(new DateTime(2026, 7, 10, 15, 0, 0, DateTimeKind.Utc), DemoMode.NextResetUtc(now));
    }

    [TestMethod]
    public void NextResetUtc_ExactlyOnTheHourReturnsTheFollowingHour_NotNow()
    {
        // The reset is firing right now. Returning `now` would make TimeUntilReset zero
        // and freeze the banner at "now" for a full second; returning the following hour
        // is what lets the countdown restart cleanly once the instance is back.
        var onTheHour = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(new DateTime(2026, 7, 10, 15, 0, 0, DateTimeKind.Utc), DemoMode.NextResetUtc(onTheHour));
    }

    [TestMethod]
    public void NextResetUtc_RollsOverMidnightAndMonthEnd()
    {
        var lastMinuteOfTheYear = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        Assert.AreEqual(
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DemoMode.NextResetUtc(lastMinuteOfTheYear));
    }

    [TestMethod]
    public void NextResetUtc_AlwaysReturnsUtc()
    {
        // Stamped into a `data-reset-at` attribute and parsed by Date.parse() in the
        // browser. An Unspecified Kind would serialise without the trailing Z and every
        // non-UTC visitor would see a countdown skewed by their offset.
        var result = DemoMode.NextResetUtc(new DateTime(2026, 7, 10, 14, 17, 43, DateTimeKind.Utc));
        Assert.AreEqual(DateTimeKind.Utc, result.Kind);
    }

    [TestMethod]
    public void TimeUntilReset_CountsDownWithinTheHour()
    {
        var now = new DateTime(2026, 7, 10, 14, 17, 43, DateTimeKind.Utc);
        Assert.AreEqual(TimeSpan.FromSeconds((42 * 60) + 17), DemoMode.TimeUntilReset(now));
    }

    [TestMethod]
    public void TimeUntilReset_NeverExceedsAnHourAndIsNeverNegative()
    {
        // Walk a full hour a second at a time: the banner must never render a negative
        // duration, and never a value that would format past "59:59".
        var start = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc);
        for (var second = 0; second < 3600; second++)
        {
            var remaining = DemoMode.TimeUntilReset(start.AddSeconds(second));
            Assert.IsTrue(remaining > TimeSpan.Zero, $"remaining was {remaining} at +{second}s");
            Assert.IsTrue(remaining <= TimeSpan.FromHours(1), $"remaining was {remaining} at +{second}s");
        }
    }
}
