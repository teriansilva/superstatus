using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities.Identity;
using SuperStatus.ServiceDefaults;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #377 — the demo instance's Identity behaviour, driven through the real HTTP
/// pipeline (same harness as <see cref="IdentityFirstRunTests"/>).
///
/// <para>Three things are proven here that nothing else can prove:</para>
/// <list type="number">
///   <item>With <c>PUBLIC_DEMO=true</c>, an admin is seeded and can actually sign in with
///   a five-character password — i.e. the hash-bypass seed really does work against the
///   registered twelve-character policy, which is the one genuinely unusual thing in
///   this feature.</item>
///   <item>With the flag off, no user is seeded, and the rendered login HTML contains no
///   trace of the demo credentials. This is the security invariant.</item>
///   <item>The seed never touches a database that already has users.</item>
/// </list>
///
/// <para><c>PUBLIC_DEMO</c> is read from the process environment inside Identity's
/// <c>Program</c> (before the request pipeline exists), so it must be set before the
/// factory builds its host — it cannot be injected. MSTest runs test classes
/// sequentially by default; the env var is set and cleared around this class.</para>
/// </summary>
[TestClass]
public class DemoInstanceIdentityTests
{
    private static readonly InMemoryDatabaseRoot InMemoryRoot = new();

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        // Same reasoning as IdentityFirstRunTests: InMemory can't migrate, and leaving
        // WEBAPP_HTTP/IDP_PUBLIC_HTTP unset selects dynamic self-host mode.
        Environment.SetEnvironmentVariable("APPLY_MIGRATIONS", "false");
        Environment.SetEnvironmentVariable("WEBAPP_HTTP", null);
        Environment.SetEnvironmentVariable("IDP_PUBLIC_HTTP", null);
        Environment.SetEnvironmentVariable("IDP_HTTP", "http://identity:8080");
    }

    [ClassCleanup]
    public static void ClassCleanup()
        => Environment.SetEnvironmentVariable(DemoMode.EnvironmentVariable, null);

    private static void SetDemoMode(bool enabled)
        => Environment.SetEnvironmentVariable(DemoMode.EnvironmentVariable, enabled ? "true" : null);

    private static WebApplicationFactory<SuperStatus.Identity.TestEntryPoint> CreateFactory(string dbName)
    {
        return new WebApplicationFactory<SuperStatus.Identity.TestEntryPoint>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:SuperStatusIdentityDb"] = "Host=in-memory"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    // See IdentityFirstRunTests for why every IDbContextOptionsConfiguration
                    // descriptor has to go, not just the options themselves (#88).
                    var toRemove = services.Where(s =>
                        s.ServiceType == typeof(DbContextOptions<SuperStatusIdentityDb>) ||
                        s.ServiceType == typeof(DbContextOptions) ||
                        s.ServiceType == typeof(SuperStatusIdentityDb) ||
                        (s.ServiceType.IsGenericType
                            && s.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextOptionsConfiguration<>)
                            && s.ServiceType.GenericTypeArguments[0] == typeof(SuperStatusIdentityDb))).ToList();
                    foreach (var d in toRemove) services.Remove(d);

                    services.AddDbContext<SuperStatusIdentityDb>(opts => opts
                        .UseInMemoryDatabase(dbName, InMemoryRoot)
                        .UseOpenIddict()
                        .EnableServiceProviderCaching(false));
                });
            });
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        var body = await client.GetStringAsync(path);
        var match = Regex.Match(body, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"");
        Assert.IsTrue(match.Success, $"Antiforgery token not found in {path}");
        return match.Groups["v"].Value;
    }

    // ---------------------------------------------------------------------
    // Happy path — the demo instance
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Startup_WhenDemoEnabled_SeedsAdministratorWithTheWellKnownEmail()
    {
        SetDemoMode(true);
        using var factory = CreateFactory(nameof(Startup_WhenDemoEnabled_SeedsAdministratorWithTheWellKnownEmail));
        using var client = factory.CreateClient(); // forces the host (and its seed) to build

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SuperStatusIdentityUser>>();

        var users = await userManager.Users.ToListAsync();
        Assert.AreEqual(1, users.Count, "Demo mode must seed exactly one administrator.");
        Assert.AreEqual(SuperStatusIdentityDbInitializer.DemoAdminEmail, users[0].Email);

        var roles = await userManager.GetRolesAsync(users[0]);
        CollectionAssert.Contains(
            roles.ToList(),
            SuperStatusIdentityDbInitializer.AdministratorRole,
            "The seeded demo user must be an Administrator, or the demo shows a read-only console.");
    }

    [TestMethod]
    public async Task LoginPost_WhenDemoEnabled_SignsInWithTheSubPolicyPassword()
    {
        // The load-bearing test. The registered password policy requires 12 characters and
        // the demo password is 5, so the seed sets PasswordHash directly rather than going
        // through CreateAsync(user, password). If that bypass were wrong — a mismatched
        // hasher, a missing SecurityStamp, an unconfirmed email — the user would exist but
        // could never log in, and every other assertion here would still pass.
        SetDemoMode(true);
        using var factory = CreateFactory(nameof(LoginPost_WhenDemoEnabled_SignsInWithTheSubPolicyPassword));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.IsTrue(
            SuperStatusIdentityDbInitializer.DemoAdminPassword.Length < 12,
            "This test is only meaningful while the demo password is below the 12-char policy.");

        var token = await AntiforgeryTokenAsync(client, "/Identity/Account/Login");
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Input.Email", SuperStatusIdentityDbInitializer.DemoAdminEmail),
            new KeyValuePair<string, string>("Input.Password", SuperStatusIdentityDbInitializer.DemoAdminPassword),
            new KeyValuePair<string, string>("Input.RememberMe", "false"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync("/Identity/Account/Login", form);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode,
            $"Demo login must succeed. Body: {await response.Content.ReadAsStringAsync()}");
        Assert.IsTrue(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(c => c.StartsWith(".AspNetCore.Identity.Application", StringComparison.Ordinal)),
            "No Identity application cookie was issued — the seeded password hash does not verify.");
    }

    [TestMethod]
    public async Task LoginGet_WhenDemoEnabled_AdvertisesCredentialsAndPrefillsBothFields()
    {
        SetDemoMode(true);
        using var factory = CreateFactory(nameof(LoginGet_WhenDemoEnabled_AdvertisesCredentialsAndPrefillsBothFields));
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Identity/Account/Login");

        StringAssert.Contains(html, "hud-demo-creds", "The demo credentials panel is missing.");
        StringAssert.Contains(html, SuperStatusIdentityDbInitializer.DemoAdminEmail);

        // Prefill: the visitor should be able to press Sign in without typing.
        StringAssert.Contains(html, $"value=\"{SuperStatusIdentityDbInitializer.DemoAdminEmail}\"");
        StringAssert.Contains(html, $"value=\"{SuperStatusIdentityDbInitializer.DemoAdminPassword}\"");

        // The countdown target is stamped server-side so demo-countdown.js never has to
        // know the schedule.
        StringAssert.Contains(html, "data-demo-reset-at=");
        StringAssert.Contains(html, "data-demo-countdown");
    }

    // ---------------------------------------------------------------------
    // The invariant — a normal deployment
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Startup_WhenDemoDisabled_SeedsNoUserAndLoginStillRedirectsToSetup()
    {
        SetDemoMode(false);
        using var factory = CreateFactory(nameof(Startup_WhenDemoDisabled_SeedsNoUserAndLoginStillRedirectsToSetup));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Identity/Account/Login");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        StringAssert.Contains(response.Headers.Location!.ToString(), "/Identity/Account/Setup");

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SuperStatusIdentityUser>>();
        Assert.AreEqual(0, await userManager.Users.CountAsync(),
            "Without PUBLIC_DEMO no user may be seeded — the operator creates the first admin through /Setup.");
    }

    [TestMethod]
    public async Task LoginGet_WhenDemoDisabled_LeaksNoDemoCredentialsIntoTheHtml()
    {
        // The security invariant, asserted on the rendered bytes rather than on the flag:
        // a real deployment's login page must not so much as name the demo account.
        SetDemoMode(false);
        using var factory = CreateFactory(nameof(LoginGet_WhenDemoDisabled_LeaksNoDemoCredentialsIntoTheHtml));
        await SeedRealUserAsync(factory); // so /Login renders the form instead of redirecting to /Setup
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Identity/Account/Login");

        Assert.IsFalse(html.Contains(SuperStatusIdentityDbInitializer.DemoAdminEmail, StringComparison.OrdinalIgnoreCase),
            "A non-demo login page must never render the demo email.");
        Assert.IsFalse(html.Contains("hud-demo-creds", StringComparison.Ordinal),
            "A non-demo login page must not render the credentials panel.");
        Assert.IsFalse(html.Contains("hud-demo-prefilled", StringComparison.Ordinal),
            "A non-demo login page must not mark its fields as prefilled.");
        Assert.IsFalse(html.Contains("data-demo-reset-at", StringComparison.Ordinal),
            "A non-demo page must not stamp a reset countdown target.");
        Assert.IsFalse(html.Contains("demo-countdown.js", StringComparison.Ordinal),
            "A non-demo page must not load the countdown script.");
    }

    [TestMethod]
    public async Task Seed_WhenDemoEnabledButUsersAlreadyExist_LeavesThemAlone()
    {
        // Ordinary container restarts re-run the seed against a populated DB (the volume
        // only dies on the hourly reset). It must be a no-op, and it must never be able to
        // inject a weak admin into a database that already belongs to someone.
        SetDemoMode(false);
        using var factory = CreateFactory(nameof(Seed_WhenDemoEnabledButUsersAlreadyExist_LeavesThemAlone));
        await SeedRealUserAsync(factory);

        await SuperStatusIdentityDbInitializer.Seed(
            factory.Services, applyMigrations: false, seedDemoAdmin: true);

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SuperStatusIdentityUser>>();
        var users = await userManager.Users.ToListAsync();

        Assert.AreEqual(1, users.Count, "The demo seed must not add a user to a populated database.");
        Assert.AreEqual("real-operator@superstatus.test", users[0].Email);
    }

    private static async Task SeedRealUserAsync(WebApplicationFactory<SuperStatus.Identity.TestEntryPoint> factory)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SuperStatusIdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roleManager.RoleExistsAsync(SuperStatusIdentityDbInitializer.AdministratorRole))
        {
            await roleManager.CreateAsync(new IdentityRole(SuperStatusIdentityDbInitializer.AdministratorRole));
        }

        var user = new SuperStatusIdentityUser
        {
            UserName = "real-operator@superstatus.test",
            Email = "real-operator@superstatus.test",
            EmailConfirmed = true
        };
        var result = await userManager.CreateAsync(user, "a-real-operator-password");
        Assert.IsTrue(result.Succeeded,
            $"Seed user creation failed: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        await userManager.AddToRoleAsync(user, SuperStatusIdentityDbInitializer.AdministratorRole);
    }
}
