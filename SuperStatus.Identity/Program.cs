using Microsoft.AspNetCore.HttpOverrides;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Identity.Services;

var builder = WebApplication.CreateBuilder(args);
ConfigureServices(builder);
ConfigureReverseProxy(builder);
var app = builder.Build();
await SuperStatusIdentityDbInitializer.Seed(app.Services, isDevEnvironment: true);
ConfigureMiddleware(app);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
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


