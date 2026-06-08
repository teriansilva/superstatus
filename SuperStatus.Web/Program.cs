using Duende.AccessTokenManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;
using SuperStatus.ServiceDefaults;
using SuperStatus.Web;
using SuperStatus.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();


builder.Services.AddOutputCache();

builder.Services.AddHttpContextAccessor();

// OIDC_WEB_CLIENT_SECRET must match the value baked into the OpenIddict
// application registration in SuperStatusIdentityDbInitializer. The
// "some_secret" fallback covers the dev/Aspire path where the env var is
// not set; production deployments should always set it explicitly.
var oidcWebClientSecret =
    Environment.GetEnvironmentVariable("OIDC_WEB_CLIENT_SECRET")
    ?? "some_secret";

// IDP_HTTP is the internal/back-channel authority this service uses to reach
// Identity (discovery, token, userinfo, JWKS). IDP_PUBLIC_HTTP is the authority
// the *browser* is sent to for the interactive login; it falls back to IDP_HTTP
// when they're the same host (the reverse-proxy deployment). When they differ
// (the single-host self-host stack), Identity pins IDP_PUBLIC_HTTP as its issuer
// and IdpBackchannelRewriteHandler retargets this service's own back-channel
// calls from the public host to the reachable internal one.
var idpInternal = Environment.GetEnvironmentVariable("IDP_HTTP");
var idpPublic =
    Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP") ?? idpInternal;

// Fail fast on the classic misconfiguration — a loopback public issuer paired
// with an HTTPS back-channel authority (a TLS deploy that left IDP_PUBLIC_HTTP at
// the localhost default).
IdpAuthority.Validate(idpPublic, idpInternal);

// Cookie / response-mode relaxation is required only when the BROWSER is on plain
// HTTP — that's when Secure/SameSite=None cookies are never returned. It is keyed
// on the public authority's scheme, NOT on "public != internal", so a TLS proxy
// in front of the self-host compose (public HTTPS, internal compose name) keeps
// secure cookies while still getting the back-channel rewrite below.
var browserUsesPlainHttp = IdpAuthority.BrowserUsesPlainHttp(idpPublic);

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme =
        CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme =
        OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    // The post-login session cookie must also be returnable over plain HTTP on
    // the trial stack; otherwise the user is signed in but never seen as
    // authenticated. Untouched in any HTTPS deployment.
    if (browserUsesPlainHttp)
    {
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    }
})
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.SignInScheme = "Cookies";
    // Browser-facing authority: the issuer the browser is redirected to and the
    // value issuer / RFC 9207 iss validation is performed against.
    options.Authority = idpPublic;
    options.ClientId = "aspNetCoreAuth";
    options.ClientSecret = oidcWebClientSecret;
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";
    options.SignedOutRedirectUri = "/";
    options.RequireHttpsMetadata = false; //Todo: set to true in production
    options.GetClaimsFromUserInfoEndpoint = true;
    // Retarget this service's own back-channel calls (discovery/token/userinfo)
    // from the public authority to the reachable internal one. Installed whenever
    // the two authorities differ (both the localhost trial and a TLS proxy in
    // front of the compose stack); not installed when they're equal.
    if (IdpBackchannelRewriteHandler.TryCreate(idpPublic, idpInternal) is { } rewrite)
    {
        options.BackchannelHttpHandler = rewrite;
    }

    // Over plain HTTP the web app (localhost) and identity server (id.localhost)
    // are different sites, and the default correlation/nonce cookies are
    // SameSite=None + Secure — which a browser never returns over HTTP, so
    // correlation fails. Use Lax, non-Secure cookies plus a query-mode callback
    // (a top-level GET, on which Lax cookies ARE sent cross-site) so the login
    // completes. Keyed on the browser scheme, so any HTTPS deployment keeps the
    // secure defaults and form_post.
    if (browserUsesPlainHttp)
    {
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.None;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.None;
    }
});

// Client credentials token management (machine-to-machine)
builder.Services
    .AddClientCredentialsTokenManagement()
    .AddClient(ClientCredentialsClientName.Parse("apiservice"), client =>
    {
        // Client-credentials is a pure back-channel flow — use the internal
        // authority directly (no browser involved, no rewrite needed).
        client.TokenEndpoint = new Uri($"{idpInternal}/connect/token");

        client.ClientId = ClientId.Parse("aspNetCoreAuth");
        client.ClientSecret = ClientSecret.Parse(oidcWebClientSecret);
        client.Scope = Scope.Parse("api");
    });

// Typed HttpClient that automatically requests/refreshes app tokens
builder.Services.AddClientCredentialsHttpClient(
    "apiservice",
    ClientCredentialsClientName.Parse("apiservice"),
    c => c.BaseAddress = new Uri("https+http://apiservice"));

// Issue #126/#139: Development-only "demo mode" — when SUPERSTATUS_DEMO=1, the
// API clients are fed seeded sample data by DemoApiHandler instead of the real
// API, so the visual harness can render the actual pages with no backend. Never
// active outside Development.
var demoMode = builder.Environment.IsDevelopment()
    && Environment.GetEnvironmentVariable("SUPERSTATUS_DEMO") == "1";

if (demoMode)
{
    static HttpClient DemoClient() =>
        new(new SuperStatus.Web.DemoData.DemoApiHandler()) { BaseAddress = new Uri("http://demo.local") };
    builder.Services.AddTransient(_ => new StatusApiClient(DemoClient()));
    builder.Services.AddTransient(_ => new IncidentApiClient(DemoClient()));
    builder.Services.AddTransient(_ => new SettingsApiClient(DemoClient()));
    builder.Services.AddTransient(_ => new UpdatesApiClient(DemoClient()));
    builder.Services.AddTransient(_ => new PushApiClient(DemoClient()));
}
else
{
    // Wire the named client into your typed client
    builder.Services.AddTransient(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return new StatusApiClient(factory.CreateClient("apiservice"));
    });

    builder.Services.AddTransient(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return new IncidentApiClient(factory.CreateClient("apiservice"));
    });

    builder.Services.AddTransient(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return new SettingsApiClient(factory.CreateClient("apiservice"));
    });

    builder.Services.AddTransient(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return new UpdatesApiClient(factory.CreateClient("apiservice"));
    });

    builder.Services.AddTransient(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return new PushApiClient(factory.CreateClient("apiservice"));
    });
}


builder.Services.AddMudServices();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.UseOutputCache();

app.UseAuthentication();
app.UseAuthorization();

// Login endpoint to explicitly trigger OIDC challenge
app.MapGet("/login", (string? returnUrl) =>
    Results.Challenge(
        new AuthenticationProperties
        {
            RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl
        },
        new[] { OpenIdConnectDefaults.AuthenticationScheme }
    ));

app.MapGet("/logout", () =>
    Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        new[]
        {
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme
        }
    )
);

// Issue #269: SEO/GEO discovery files, served per-instance with absolute URLs
// derived from the request host so they're correct on any self-hoster domain.
app.MapGet("/robots.txt", (HttpContext ctx) =>
{
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    // Public status page is indexable; the operator console + auth flows are not.
    var body =
        "User-agent: *\n" +
        "Disallow: /admin\n" +
        "Disallow: /Account\n" +
        "Disallow: /Identity\n" +
        "Allow: /\n\n" +
        $"Sitemap: {origin}/sitemap.xml\n";
    return Results.Text(body, "text/plain");
});

app.MapGet("/sitemap.xml", (HttpContext ctx) =>
{
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var xml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n" +
        $"  <url><loc>{origin}/</loc><changefreq>hourly</changefreq><priority>1.0</priority></url>\n" +
        "</urlset>\n";
    return Results.Text(xml, "application/xml");
});

// GEO: a concise, branded plain-text summary for LLM crawlers.
app.MapGet("/llms.txt", async (HttpContext ctx, SettingsApiClient settings) =>
{
    var s = await settings.GetSettingsAsync();
    var name = string.IsNullOrWhiteSpace(s.Title) ? "This service" : s.Title!.Trim();
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var body =
        $"# {name} — Service Status\n\n" +
        $"> The public status page for {name}: the current state of its monitored services, " +
        "uptime, and a history of incidents. Published with SuperStatus.\n\n" +
        "## What this is\n" +
        $"- The official status page for {name}.\n" +
        "- Shows which services are operational, degraded, or down, plus uptime and past incidents.\n" +
        "- Powered by SuperStatus — open-source, self-hostable status-page software.\n\n" +
        "## Links\n" +
        $"- Status page: {origin}/\n" +
        "- About SuperStatus: https://superstatus.superstatus.io/\n";
    return Results.Text(body, "text/plain");
});

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.MapStaticAssets();

app.Run();
