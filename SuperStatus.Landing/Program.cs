using SuperStatus.Landing.Components;

var builder = WebApplication.CreateBuilder(args);

// Aspire/ServiceDefaults — gives us the /health + /alive endpoints
// (MapDefaultEndpoints below) that the container healthcheck + deploy
// workflow poll.
builder.AddServiceDefaults();

// Static server-side rendering only — the landing page is brochure content
// built from the shared design-system CSS. No interactive components, so no SignalR.
builder.Services.AddRazorComponents();

builder.Services.AddOutputCache();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// TLS is terminated at the reverse proxy; the container speaks plain HTTP, so
// no HttpsRedirection here (it would loop behind the proxy). ForwardedHeaders
// are enabled via env in the compose file.
app.UseStaticFiles();
app.UseAntiforgery();
app.UseOutputCache();

app.MapRazorComponents<App>();

app.MapDefaultEndpoints();

app.MapStaticAssets();

app.Run();
