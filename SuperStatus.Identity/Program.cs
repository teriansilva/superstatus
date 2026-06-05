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

    builder.Services.AddSuperStatusIdentityServices(builder.Configuration);

    services.AddHttpContextAccessor();

    // Add MVC so AuthorizationController endpoints are mapped.
    services.AddControllersWithViews();

    builder.Services.AddRazorPages();
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


