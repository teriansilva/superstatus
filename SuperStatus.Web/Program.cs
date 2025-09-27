using Duende.AccessTokenManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MudBlazor.Services;
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

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme =
        CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme =
        OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.SignInScheme = "Cookies";
    options.Authority = Environment.GetEnvironmentVariable("IDP_HTTP");
    options.ClientId = "aspNetCoreAuth";
    options.ClientSecret = "some_secret";
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";
    options.SignedOutRedirectUri = "/";
    options.RequireHttpsMetadata = false; //Todo: set to true in production
    options.GetClaimsFromUserInfoEndpoint = true;
});

// Client credentials token management (machine-to-machine)
builder.Services
    .AddClientCredentialsTokenManagement()
    .AddClient(ClientCredentialsClientName.Parse("apiservice"), client =>
    {
        var authority = Environment.GetEnvironmentVariable("IDP_HTTP");
        client.TokenEndpoint = new Uri($"{authority}/connect/token");

        client.ClientId = ClientId.Parse("aspNetCoreAuth");
        client.ClientSecret = ClientSecret.Parse("some_secret");
        client.Scope = Scope.Parse("api");
    });

// Typed HttpClient that automatically requests/refreshes app tokens
builder.Services.AddClientCredentialsHttpClient(
    "apiservice",
    ClientCredentialsClientName.Parse("apiservice"),
    c => c.BaseAddress = new Uri("https+http://apiservice"));

// Wire the named client into your typed client
builder.Services.AddTransient(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new StatusApiClient(factory.CreateClient("apiservice"));
});


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

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.MapStaticAssets();

app.Run();
