using Microsoft.AspNetCore.HttpOverrides;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Identity.Services;

var builder = WebApplication.CreateBuilder(args);
ConfigureServices(builder);
ConfigureReverseProxy(builder);
var app = builder.Build();

// Migrations + identity seed run before the request pipeline starts.
//   APPLY_MIGRATIONS=false to opt out (e.g. if a migration is suspect).
// The first admin user is no longer seeded from env vars — on a fresh DB
// the operator creates it through the UI: visiting /Identity/Account/Login
// when the user table is empty redirects to /Identity/Account/Setup (see
// Areas/Identity/Pages/Account/Setup.cshtml.cs).
var applyMigrations = (Environment.GetEnvironmentVariable("APPLY_MIGRATIONS") ?? "true")
    .Equals("true", StringComparison.OrdinalIgnoreCase);

await SuperStatusIdentityDbInitializer.Seed(
    app.Services,
    applyMigrations: applyMigrations);

ConfigureMiddleware(app);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    // #154: re-execute the OIDC-aware ErrorController route ("/error"). The
    // colliding Razor Pages /Error page was removed — both matched "/error"
    // case-insensitively and threw AmbiguousMatchException.
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.Run();
return;

void ConfigureServices(WebApplicationBuilder applicationBuilder)
{
    builder.AddServiceDefaults();
    IServiceCollection services = applicationBuilder.Services;

    // #326: persist DataProtection keys to a mounted volume (when
    // DATAPROTECTION_KEYS_DIR is set) so antiforgery tokens + the Identity auth
    // cookie survive a container recreate. Without this, every restart (the
    // installer's Watchtower auto-update, `compose pull`) rotates the keyring and
    // the next form POST — first-run /Identity/Account/Setup — fails antiforgery
    // with HTTP 400. No-op in dev (env unset).
    builder.AddSuperStatusDataProtection("SuperStatus.Identity");

    builder.Services.AddSuperStatusIdentityServices(builder.Configuration);

    services.AddHttpContextAccessor();

    // Add MVC so AuthorizationController endpoints are mapped.
    services.AddControllersWithViews();

    builder.Services.AddRazorPages();

    // On the single-host self-host (start.sh / HOST_IP=…), Identity and Web are
    // published on the SAME host, different ports. Browser cookies are scoped by
    // host and ignore the port, and the framework's default antiforgery cookie
    // name is derived from the content root — identical "/app" in both container
    // images — so both apps would issue a cookie of the same name for the same
    // host, protected with different data-protection keys. A Web request then
    // clobbers Identity's antiforgery cookie, and the next Identity form POST
    // (e.g. first-run /Identity/Account/Setup — issue #280) fails validation with
    // HTTP 400. Give each app a distinct cookie name so the two coexist. Harmless
    // on the reverse-proxy deploy (distinct hostnames). Keep in sync with
    // SuperStatus.Web.
    services.AddAntiforgery(options => options.Cookie.Name = ".SuperStatus.Identity.Antiforgery");
}

static void ConfigureMiddleware(WebApplication app)
{
    app.UseForwardedHeaders();

    app.UseStaticFiles();

    app.UseRouting();

    // Needed for Identity cookies and OpenIddict server.
    app.UseAuthentication();
    app.UseAuthorization();

    // Map endpoints
    app.MapRazorPages();
    app.MapControllers();

    app.MapDefaultEndpoints();
}

void ConfigureReverseProxy(WebApplicationBuilder applicationBuilder)
{
    applicationBuilder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.AddServerHeader = false;
    });

    applicationBuilder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedPrefix;
    });
}

namespace SuperStatus.Identity
{
    // Marker for WebApplicationFactory<T> in SuperStatus.Tests — any public
    // type in this assembly works, but a dedicated marker keeps the intent
    // explicit and avoids racing the (auto-generated, internal) Program
    // class produced by top-level statements.
    public sealed class TestEntryPoint;
}


