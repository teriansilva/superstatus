using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
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
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
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
    
    // Request API scope so user access token can call the API
    options.Scope.Add("api");
});

// User access token management (for logged-in users)
builder.Services.AddOpenIdConnectAccessTokenManagement();

// Named HttpClient WITHOUT token handler for anonymous endpoints
builder.Services.AddHttpClient("apiservice-anon", c =>
    c.BaseAddress = new Uri("https+http://apiservice"));

// Named HttpClient WITH user access token handler for authorized endpoints
builder.Services.AddHttpClient("apiservice-auth", c =>
    c.BaseAddress = new Uri("https+http://apiservice"))
    .AddUserAccessTokenHandler();

// Typed clients - pass IHttpClientFactory so they can choose the right client
builder.Services.AddTransient(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var authStateProvider = sp.GetRequiredService<AuthenticationStateProvider>();
    return new StatusApiClient(factory, authStateProvider);
});

builder.Services.AddTransient(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new IncidentApiClient(factory);
});

builder.Services.AddTransient(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    return new ConfigurationApiClient(factory, httpContextAccessor);
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
