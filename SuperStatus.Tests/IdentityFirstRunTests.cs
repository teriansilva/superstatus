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

namespace SuperStatus.Tests;

[TestClass]
public class IdentityFirstRunTests
{
    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        // The Identity site reads these directly from process env on boot.
        // Migrations are not supported on the InMemory provider, and a set
        // WEBAPP_HTTP would push the seeder into creating the OIDC client
        // (also unnecessary for the routing checks below).
        Environment.SetEnvironmentVariable("APPLY_MIGRATIONS", "false");
        Environment.SetEnvironmentVariable("WEBAPP_HTTP", null);
        // #358: WEBAPP_HTTP + IDP_PUBLIC_HTTP both unset ⇒ dynamic self-host mode,
        // so the dynamic OpenIddict host-gate + same-origin handlers are wired.
        Environment.SetEnvironmentVariable("IDP_PUBLIC_HTTP", null);
        // #358 (ID2088): in dynamic mode the issuer is pinned to the internal
        // authority (IDP_HTTP) so authorize + token agree on it.
        Environment.SetEnvironmentVariable("IDP_HTTP", "http://identity:8080");
    }

    // EF's InMemory provider keys its store by (databaseName, internal
    // service provider). When EnableServiceProviderCaching is false, every
    // DbContext instantiation gets a fresh internal service provider, which
    // would spawn a fresh empty store — so a row seeded in one scope would
    // be invisible to a later HTTP request. An explicit
    // InMemoryDatabaseRoot pins the store identity so every DbContext in
    // the test sees the same data, while caching-off still keeps the
    // InMemory + Npgsql internal providers from colliding inside EF Core.
    private static readonly InMemoryDatabaseRoot InMemoryRoot = new();

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
                        // The real Postgres connection string is unreachable
                        // from the test; we swap the DbContext below so this
                        // value is never consulted, but the registration in
                        // AddSuperStatusIdentityDb reads it during builder
                        // setup. A non-empty placeholder is enough.
                        ["ConnectionStrings:SuperStatusIdentityDb"] = "Host=in-memory"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    // Issue #88. The production registration in
                    // AddSuperStatusIdentityDb wires EF Core to Npgsql via
                    // `services.AddDbContext<…>(o => o.UseNpgsql(...))`. That
                    // call registers two things: the options object itself
                    // (`DbContextOptions<T>` + `DbContextOptions`) AND the
                    // *configuration delegate* on the options pipeline as
                    // `IDbContextOptionsConfiguration<T>`. Removing only the
                    // options leaves the Npgsql `UseNpgsql` lambda in the
                    // pipeline; the next `AddDbContext` call (InMemory) appends
                    // its own provider on top and EF refuses to build the
                    // context with "Services for database providers
                    // 'Npgsql.EntityFrameworkCore.PostgreSQL',
                    // 'Microsoft.EntityFrameworkCore.InMemory' have been
                    // registered. Only a single database provider can be
                    // registered in a service provider."
                    //
                    // Fix: also strip every `IDbContextOptionsConfiguration<T>`
                    // descriptor for this context, so the InMemory replacement
                    // is the *only* configuration delegate in the chain.
                    var toRemove = services.Where(s =>
                        s.ServiceType == typeof(DbContextOptions<SuperStatusIdentityDb>) ||
                        s.ServiceType == typeof(DbContextOptions) ||
                        s.ServiceType == typeof(SuperStatusIdentityDb) ||
                        (s.ServiceType.IsGenericType
                            && s.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextOptionsConfiguration<>)
                            && s.ServiceType.GenericTypeArguments[0] == typeof(SuperStatusIdentityDb))).ToList();
                    foreach (var d in toRemove) services.Remove(d);

                    // Pass an explicit InMemoryDatabaseRoot so every DbContext
                    // spawned during the test (seed scope + each HTTP request
                    // scope) sees the same store; EnableServiceProviderCaching(false)
                    // keeps each DbContext's internal SP isolated from the
                    // cached Npgsql one without losing OpenIddict's storage
                    // extensions (which UseInternalServiceProvider would).
                    services.AddDbContext<SuperStatusIdentityDb>(opts => opts
                        .UseInMemoryDatabase(dbName, InMemoryRoot)
                        .UseOpenIddict()
                        .EnableServiceProviderCaching(false));
                });
            });
    }

    private static async Task SeedUserAsync(WebApplicationFactory<SuperStatus.Identity.TestEntryPoint> factory)
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
            UserName = "seeded@superstatus.test",
            Email = "seeded@superstatus.test",
            EmailConfirmed = true
        };
        var createResult = await userManager.CreateAsync(user, "seeded-password-xyz");
        Assert.IsTrue(createResult.Succeeded,
            $"Seed user creation failed: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
        await userManager.AddToRoleAsync(user, SuperStatusIdentityDbInitializer.AdministratorRole);
    }

    [TestMethod]
    public async Task LoginGet_WithZeroUsers_RedirectsToSetup()
    {
        using var factory = CreateFactory(nameof(LoginGet_WithZeroUsers_RedirectsToSetup));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Identity/Account/Login");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsNotNull(response.Headers.Location);
        StringAssert.Contains(response.Headers.Location!.ToString(), "/Identity/Account/Setup");
    }

    [TestMethod]
    public async Task SetupGet_WithZeroUsers_Returns200()
    {
        using var factory = CreateFactory(nameof(SetupGet_WithZeroUsers_Returns200));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Identity/Account/Setup");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ErrorGet_ResolvesToSingleEndpoint_Returns200()
    {
        // #154 regression: ErrorController [Route("error")] and the old Razor
        // page /Error both matched "/error" case-insensitively → 500
        // AmbiguousMatchException. The page was removed; /error must now resolve
        // to the single (anonymous) ErrorController and render the error view.
        using var factory = CreateFactory(nameof(ErrorGet_ResolvesToSingleEndpoint_Returns200));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/error");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "/error must resolve to one endpoint (not 500 AmbiguousMatchException) and render anonymously.");
    }

    [TestMethod]
    public async Task RegisterGet_AlwaysReturns404()
    {
        using var factory = CreateFactory(nameof(RegisterGet_AlwaysReturns404));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Identity/Account/Register");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ForgotPasswordGet_AlwaysReturns404()
    {
        using var factory = CreateFactory(nameof(ForgotPasswordGet_AlwaysReturns404));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Identity/Account/ForgotPassword");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task SetupGet_WithOneUser_Returns404()
    {
        using var factory = CreateFactory(nameof(SetupGet_WithOneUser_Returns404));
        await SeedUserAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Identity/Account/Setup");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task LoginGet_WithOneUser_Returns200()
    {
        using var factory = CreateFactory(nameof(LoginGet_WithOneUser_Returns200));
        await SeedUserAsync(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Identity/Account/Login");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task SetupGet_SetsDistinctlyNamedAntiforgeryCookie()
    {
        // #280 regression: on the single-host self-host (start.sh / HOST_IP=…),
        // Web and Identity are published on the SAME host (different ports).
        // Browser cookies ignore the port, and the framework's default antiforgery
        // cookie name is derived from the content root — identical "/app" in both
        // container images — so a default name collides across the two apps. A Web
        // request then clobbers Identity's antiforgery cookie and the Setup POST
        // fails antiforgery validation with HTTP 400. Identity must namespace its
        // antiforgery cookie so the two coexist on a shared host.
        using var factory = CreateFactory(nameof(SetupGet_SetsDistinctlyNamedAntiforgeryCookie));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Identity/Account/Setup");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsTrue(response.Headers.TryGetValues("Set-Cookie", out var cookies),
            "Setup GET must set an antiforgery cookie.");
        var cookieList = cookies.ToList();
        Assert.IsTrue(
            cookieList.Any(c => c.StartsWith(".SuperStatus.Identity.Antiforgery=", StringComparison.Ordinal)),
            $"Expected a '.SuperStatus.Identity.Antiforgery' cookie; got: [{string.Join(" | ", cookieList)}]");
        Assert.IsFalse(
            cookieList.Any(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)),
            "Identity must not emit the default-named antiforgery cookie — it collides with Web on a shared host (#280).");
    }

    [TestMethod]
    public async Task SetupPost_WithValidForm_CreatesAdministratorAndSignsIn()
    {
        using var factory = CreateFactory(nameof(SetupPost_WithValidForm_CreatesAdministratorAndSignsIn));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // First, GET the Setup form to obtain the antiforgery cookie + form token.
        var getResp = await client.GetAsync("/Identity/Account/Setup");
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);
        var getBody = await getResp.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(getBody,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"");
        Assert.IsTrue(tokenMatch.Success, "Antiforgery token not found in Setup form");
        var token = tokenMatch.Groups["v"].Value;

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Input.Email", "first-admin@superstatus.test"),
            new KeyValuePair<string, string>("Input.Password", "correct-horse-battery-staple"),
            new KeyValuePair<string, string>("Input.ConfirmPassword", "correct-horse-battery-staple"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var postResp = await client.PostAsync("/Identity/Account/Setup", form);

        Assert.AreEqual(HttpStatusCode.Redirect, postResp.StatusCode,
            $"Expected 302 from Setup POST, got {postResp.StatusCode}. Body: {await postResp.Content.ReadAsStringAsync()}");
        Assert.IsTrue(
            postResp.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(c => c.StartsWith(".AspNetCore.Identity.Application", StringComparison.Ordinal)),
            "Identity application cookie was not set on the redirect response.");

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SuperStatusIdentityUser>>();
        var users = await userManager.Users.ToListAsync();
        Assert.AreEqual(1, users.Count, "Expected exactly one user after Setup POST.");
        var roles = await userManager.GetRolesAsync(users[0]);
        Assert.IsTrue(
            roles.Contains(SuperStatusIdentityDbInitializer.AdministratorRole),
            $"User is missing the {SuperStatusIdentityDbInitializer.AdministratorRole} role. Actual roles: [{string.Join(", ", roles)}]");
    }

    // #358: integration proof that the dynamic same-origin redirect_uri handler is
    // actually wired into the OpenIddict authorization pipeline (not just unit-tested
    // in isolation). Drives the real /connect/authorize endpoint in dynamic mode: a
    // same-origin redirect_uri is accepted (the unauthenticated request challenges to
    // the login page), while a FOREIGN redirect_uri is rejected before any redirect —
    // OpenIddict refuses to bounce to an untrusted callback, so it never reaches login.
    [TestMethod]
    public async Task Authorize_DynamicMode_AcceptsSameOriginRedirect_RejectsForeign()
    {
        using var factory = CreateFactory(nameof(Authorize_DynamicMode_AcceptsSameOriginRedirect_RejectsForeign));
        await SeedUserAsync(factory); // so an unauthenticated authorize challenges to Login (not Setup)
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Minimal valid PKCE authorization request; the test server host is "localhost"
        // and the policy's web port defaults to 8080, so http://localhost:8080/signin-oidc
        // is same-origin. (A precomputed S256 challenge for a fixed verifier.)
        static string Authorize(string redirectUri) =>
            "/connect/authorize?client_id=aspNetCoreAuth&response_type=code&scope=openid" +
            "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256" +
            "&nonce=n0&state=s0&redirect_uri=" + Uri.EscapeDataString(redirectUri);

        var sameOrigin = await client.GetAsync(Authorize("http://localhost:8080/signin-oidc"));
        var foreign = await client.GetAsync(Authorize("http://evil.example.net:8080/signin-oidc"));

        var sameOriginLocation = sameOrigin.Headers.Location?.ToString() ?? "";
        Assert.IsTrue(
            sameOrigin.StatusCode == HttpStatusCode.Redirect
                && sameOriginLocation.Contains("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase),
            $"Same-origin authorize must be accepted and challenge to Login; got {(int)sameOrigin.StatusCode} → '{sameOriginLocation}'.");

        var foreignLocation = foreign.Headers.Location?.ToString() ?? "";
        Assert.IsFalse(
            foreignLocation.Contains("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase),
            $"Foreign redirect_uri must be rejected before any login challenge; got {(int)foreign.StatusCode} → '{foreignLocation}'.");
    }

    // #358 (ID2088 regression): in dynamic mode the OIDC issuer must be PINNED to the
    // internal authority (IDP_HTTP), so the front-channel authorization-issued code and
    // the back-channel token exchange stamp the SAME issuer. Without the pin OpenIddict
    // derives the issuer from the differing per-channel request host and rejects the code
    // redemption with ID2088 ("the issuer associated to the specified token is not valid"),
    // which broke the whole login end-to-end.
    [TestMethod]
    public async Task Discovery_DynamicMode_PinsIssuerToInternalHost()
    {
        using var factory = CreateFactory(nameof(Discovery_DynamicMode_PinsIssuerToInternalHost));
        using var client = factory.CreateClient();

        var doc = await client.GetStringAsync("/.well-known/openid-configuration");
        using var json = System.Text.Json.JsonDocument.Parse(doc);
        var issuer = json.RootElement.GetProperty("issuer").GetString()?.TrimEnd('/');

        Assert.AreEqual("http://identity:8080", issuer,
            $"Dynamic-mode issuer must be pinned to IDP_HTTP (http://identity:8080); discovery issuer was '{issuer}'.");
    }
}
